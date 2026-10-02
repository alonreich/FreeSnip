using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.native;
using Microsoft.Win32;
using Avalonia.Threading;

namespace freesnip.helpers;

internal static class DeploymentLifecycle
{
    private const int DeleteRetries = 3;
    private static readonly string SessionTempFolder = Path.Combine(DeploymentFootprint.DeploymentTempRoot, "Staging_" + Process.GetCurrentProcess().Id);
    private static int _pendingRebootDeletes;

    [Flags]
    private enum MoveFileFlags : uint
    {
        DelayUntilReboot = 0x00000004,
        ReplaceExisting = 0x00000001
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void LogSwallowed(string operation, string detail, Exception ex)
    {
        string suffix = ex == null ? string.Empty : " :: " + ex.Message;
        string message = $"Lifecycle swallowed failure :: {operation} :: {detail}{suffix}";
        BootstrapDebug.Log(message);
        InstallHostContext.WriteEarlyTrace(message);
    }

    public static bool IsLifecycleCommand(string[] args)
    {
        if (args == null || args.Length == 0) return false;

        return args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                             || arg.Equals("--install", StringComparison.OrdinalIgnoreCase)
                             || arg.Equals("--install-worker", StringComparison.OrdinalIgnoreCase)
                             || arg.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUninstallLauncherCommand(string[] args)
    {
        if (args == null || args.Length == 0) return false;
        return args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            && !args.Any(arg => arg.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunLifecycleCommandAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            if (IsUninstallLauncherCommand(args)) return await RunUninstallLauncherAsync(args, ct).ConfigureAwait(false);
            if (args != null && args.Any(arg => arg.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
                return await RunUninstallAsync(args, ct).ConfigureAwait(false);
            
            return await RunInstallAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BootstrapDebug.Log("Lifecycle command fatal: " + ex);
            return 1;
        }
    }

    public static async Task<int> RunInstallAsync(CancellationToken ct = default, bool? isWorkerOverride = null)
    {
        string[] cmdArgs = Environment.GetCommandLineArgs();
        bool isAutoUpdate = cmdArgs.Any(a => a.Equals("--auto-update", StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < cmdArgs.Length; i++)
        {
            if (cmdArgs[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
            {
                if (int.TryParse(cmdArgs[i + 1], out int waitPid) && waitPid > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(waitPid);
                        p.WaitForExit(15000);
                    }
                    catch { }
                }
            }
        }

        bool isWorker = isWorkerOverride ?? cmdArgs.Any(a => a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase));
        if (!isWorker)
        {
            await RelaunchInstallFromTempAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (!StartupTaskHelper.IsElevated())
        {
            string elevArgs = "--install --install-worker";
            if (isAutoUpdate) elevArgs += " --auto-update";
            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < cmdArgs.Length)
                {
                    elevArgs += $" --wait-pid {cmdArgs[i + 1]}";
                }
            }
            StartElevated(RuntimePathHelper.ExecutablePath, elevArgs);
            return 0;
        }

        using var mutex = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
        bool lockAcquired = false;
        DeploymentLogger logger = null;
        DeploymentProgress progress = null;
        string logPath = DeploymentFootprint.InstallLogPath;

        try
        {
            lockAcquired = mutex.WaitOne(0);
            if (!lockAcquired)
            {
                BootstrapDebug.Log("Install worker: another installer instance already holds the mutex, exiting.");
                InstallHostContext.WriteEarlyTrace("Install worker: installer mutex already held by another instance.");
                StartupTaskHelper.ShowForegroundMessageBox(
                    "Another FreeSnip setup is already running.\r\n\r\nPlease finish or close the other setup window first, then run this installer again.",
                    "FreeSnip Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 2;
            }

            logger = await DeploymentLogger.CreateAsync(logPath, "INSTALL/UPGRADE", ct).ConfigureAwait(false);
            progress = await DeploymentProgress.CreateAsync("FreeSnip Setup", logPath);

            string conflict = DetectConflictingSoftware();
            if (conflict != null)
            {
                await logger.LogAsync("INSTALL", "CONFLICT_WARN",
                    $"Possible conflicting software detected: {conflict}", ct).ConfigureAwait(false);
                if (!isAutoUpdate)
                {
                    var conflictChoice = await ShowBlockingPromptAsync(progress,
                        $"FreeSnip detected {conflict} running or installed on this system.\r\n\r\n" +
                        "These tools may fight over the same global hotkeys (e.g. Print Screen).\r\n\r\n" +
                        "Yes    - Continue installing FreeSnip anyway\r\n" +
                        "No     - Abort the installation",
                        "Possible Software Conflict",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning).ConfigureAwait(false);
                    if (conflictChoice == DialogResult.No)
                    {
                        await ReportAsync(progress, logger, 100, "ABORT", "CONFLICT", $"Cancelled by user due to {conflict}.", ct).ConfigureAwait(false);
                        return 0;
                    }
                }
                await logger.LogAsync("INSTALL", "CONFLICT_OVERRIDE", $"Continuing despite {conflict}", ct).ConfigureAwait(false);
            }

            bool existingFreeSnip = DetectExistingInstallation();
            bool keepUserSettings = false;
            bool cleanWipeRequested = false;

            if (isAutoUpdate)
            {
                keepUserSettings = true;
                cleanWipeRequested = false;
                await logger.LogAsync("UPGRADE", "AUTO_UPDATE", "Auto-update in progress: preserving all user settings automatically.", ct).ConfigureAwait(false);
            }
            else if (existingFreeSnip)
            {
                var upgradeChoice = await ShowBlockingPromptAsync(progress,
                    "An existing FreeSnip installation was detected on this system.\r\n\r\n" +
                    "Yes    - Upgrade and KEEP my settings (freesnip.ini)\r\n" +
                    "No     - Clean install: wipe ALL settings and user artifacts\r\n" +
                    "Cancel - Abort the installation",
                    "FreeSnip Upgrade",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question).ConfigureAwait(false);

                await logger.LogAsync("UPGRADE", "PROMPT", $"Existing install detected; user choice: {upgradeChoice}", ct).ConfigureAwait(false);
                if (upgradeChoice == DialogResult.Cancel)
                {
                    await ReportAsync(progress, logger, 100, "ABORT", "CANCELLED", "Upgrade cancelled by user.", ct).ConfigureAwait(false);
                    return 0;
                }
                keepUserSettings = upgradeChoice == DialogResult.Yes;
                cleanWipeRequested = upgradeChoice == DialogResult.No;
            }

            if (!await WaitForApplicationsToCloseAsync(progress, ct).ConfigureAwait(false)) return 0;
            bool restoreAdminStartup = keepUserSettings && (await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false) || StartupTaskHelper.DetectAdminStartupInSettingsCandidates());
            await logger.LogAsync("UPGRADE", "ELEVATION_PRESCAN", $"restoreAdminStartup={restoreAdminStartup}, keepUserSettings={keepUserSettings}", ct).ConfigureAwait(false);
            StartupTaskHelper.LogInstallationElevationState($"Pre-cleanup scan: restoreAdminStartup={restoreAdminStartup}, keepUserSettings={keepUserSettings}");
            string settingsBackupFolder = keepUserSettings ? await BackupUserSettingsAsync(logger, ct).ConfigureAwait(false) : null;
            try
            {
                StartupTaskHelper.RequireInstalledApplicationsClosed();
                await PerformFullHostCleanupAsync(progress, logger, "Pre-Install Cleanup", 5, 60, requireZeroFootprint: false, purgeUserArtifacts: cleanWipeRequested, ct).ConfigureAwait(false);

                await ReportAsync(progress, logger, 65, "DEPLOY", "PAYLOAD", "Extracting assets...", ct).ConfigureAwait(false);
                await InstallFreshAsync(progress, logger, ct).ConfigureAwait(false);
                if (settingsBackupFolder != null) await RestoreUserSettingsAsync(settingsBackupFolder, logger, ct).ConfigureAwait(false);
                await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings, restoreAdminStartup).ConfigureAwait(false);
                await LaunchInstalledApplicationAsync().ConfigureAwait(false);
                CleanupSettingsBackup(settingsBackupFolder);
                await ReportAsync(progress, logger, 100, "SUCCESS", "COMPLETE", "Deployment finalized.", ct).ConfigureAwait(false);
                if (isAutoUpdate)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                else
                {
                    await AwaitUserAcknowledgementAsync(progress, logger, "Installation complete. Click Finish to close.", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (settingsBackupFolder != null)
                    throw new IOException("Upgrade failed. Your settings recovery copy is kept at: " + settingsBackupFolder, ex);
                throw;
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.LogAsync("CRITICAL", "ERROR", ex.Message, ct, ex).ConfigureAwait(false);
            await AwaitUserAcknowledgementAsync(progress, logger, "Installation failed: " + ex.Message, CancellationToken.None).ConfigureAwait(false);
            return ex.HResult == 0 ? 1 : ex.HResult;
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    mutex.Release();
                }
                catch (SemaphoreFullException) { }
            }
            progress?.Dispose();
            if (logger != null) await logger.DisposeAsync().ConfigureAwait(false);
            if (lockAcquired)
            {
                QueueSelfCleanup(DeploymentFootprint.DeploymentTempRoot);
            }
        }
    }

    internal static async Task ScrubExplorerFileExtsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var fileExtsKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", true);
            if (fileExtsKey == null) return;

            foreach (string extName in fileExtsKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var extKey = fileExtsKey.OpenSubKey(extName, true);
                if (extKey == null) continue;

                try
                {
                    using var openWithList = extKey.OpenSubKey("OpenWithList", true);
                    if (openWithList != null)
                    {
                        var valuesToDelete = new List<string>();
                        foreach (string valName in openWithList.GetValueNames())
                        {
                            if (string.Equals(valName, "MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                            string valData = openWithList.GetValue(valName)?.ToString() ?? string.Empty;
                            if (valData.Contains("freesnip", StringComparison.OrdinalIgnoreCase))
                            {
                                valuesToDelete.Add(valName);
                            }
                        }

                        if (valuesToDelete.Count > 0)
                        {
                            string mru = openWithList.GetValue("MRUList")?.ToString() ?? string.Empty;
                            foreach (string delVal in valuesToDelete)
                            {
                                openWithList.DeleteValue(delVal, false);
                                mru = mru.Replace(delVal, string.Empty);
                                await logger.LogAsync("REGISTRY", "FILEEXTS_PURGE", $@"HKCU\...\FileExts\{extName}\OpenWithList\{delVal}", ct).ConfigureAwait(false);
                            }
                            if (!string.IsNullOrEmpty(mru))
                            {
                                openWithList.SetValue("MRUList", mru);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSwallowed("ScrubExplorerFileExts.OpenWithList", extName, ex);
                }

                try
                {
                    using var openWithProgids = extKey.OpenSubKey("OpenWithProgids", true);
                    if (openWithProgids != null)
                    {
                        foreach (string valName in openWithProgids.GetValueNames())
                        {
                            if (valName.Contains("freesnip", StringComparison.OrdinalIgnoreCase))
                            {
                                openWithProgids.DeleteValue(valName, false);
                                await logger.LogAsync("REGISTRY", "FILEEXTS_PURGE", $@"HKCU\...\FileExts\{extName}\OpenWithProgids\{valName}", ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSwallowed("ScrubExplorerFileExts.OpenWithProgids", extName, ex);
                }

                foreach (string choiceSub in new[] { "UserChoice", "UserChoiceLatest" })
                {
                    try
                    {
                        using var choiceKey = extKey.OpenSubKey(choiceSub, true);
                        if (choiceKey != null)
                        {
                            string progId = choiceKey.GetValue("ProgId")?.ToString() ?? string.Empty;
                            if (progId.Contains("freesnip", StringComparison.OrdinalIgnoreCase))
                            {
                                extKey.DeleteSubKeyTree(choiceSub, false);
                                await logger.LogAsync("REGISTRY", "FILEEXTS_CHOICE_PURGE", $@"HKCU\...\FileExts\{extName}\{choiceSub}", ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSwallowed("ScrubExplorerFileExts." + choiceSub, extName, ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("ScrubExplorerFileExtsAsync", "Root", ex);
        }
    }

    private static async Task ScrubPushNotificationsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var pushKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications\Backup", true);
            if (pushKey != null)
            {
                foreach (string subName in pushKey.GetSubKeyNames())
                {
                    if (subName.Contains(DeploymentFootprint.AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            pushKey.DeleteSubKeyTree(subName, false);
                            await logger.LogAsync("REGISTRY", "PUSH_PURGE", $@"HKCU\...\PushNotifications\Backup\{subName}", ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogSwallowed("ScrubPushNotificationsAsync", subName, ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("ScrubPushNotificationsAsync", "Root", ex);
        }
    }

    private static async Task ScrubAppCompatFlagsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var storeKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", true);
                    if (storeKey == null) continue;

                    foreach (string valName in storeKey.GetValueNames())
                    {
                        if (valName.Contains(DeploymentFootprint.AppName, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                storeKey.DeleteValue(valName, false);
                                await logger.LogAsync("REGISTRY", "APPCOMPAT_PURGE", $"{hive}\\...\\Store\\{valName}", ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                LogSwallowed("ScrubAppCompatFlagsAsync", valName, ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSwallowed("ScrubAppCompatFlagsAsync", $"{hive}\\{view}", ex);
                }
            }
        }
    }

    private static async Task ScrubTilePropertiesAsync(DeploymentLogger logger, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var tileKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Start\TileProperties", true);
            if (tileKey != null)
            {
                foreach (string sub in tileKey.GetSubKeyNames())
                {
                    if (sub.Contains(DeploymentFootprint.AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            tileKey.DeleteSubKeyTree(sub, false);
                            await logger.LogAsync("REGISTRY", "TILE_PURGE", sub, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogSwallowed("ScrubTilePropertiesAsync", sub, ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("ScrubTilePropertiesAsync", "Root", ex);
        }
    }

    public static async Task<int> RunUninstallAsync(string[] args, CancellationToken ct = default)
    {
        if (!args.Any(a => a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
            return await RunUninstallLauncherAsync(args, ct).ConfigureAwait(false);

        if (!StartupTaskHelper.IsElevated())
        {
            TryStartElevated(RuntimePathHelper.ExecutablePath, "--uninstall --cleanup-worker");
            return 0;
        }

        int parentPid = ParseParentPid(args);
        if (parentPid > 0)
        {
            BootstrapDebug.Log($"Worker: Waiting for parent PID {parentPid} to exit...");
            await WaitForParentExitAsync(parentPid, ct).ConfigureAwait(false);
        }

        using var uninstallGate = new Semaphore(1, 1, DeploymentFootprint.InstallerMutexName + "_v2");
        bool lockAcquired = false;
        string logPath = Path.Combine(SessionTempFolder, "freesnip_Uninstall.log");
        DeploymentLogger logger = null;
        DeploymentProgress progress = null;

        try
        {
            lockAcquired = uninstallGate.WaitOne(0);
            if (!lockAcquired)
            {
                StartupTaskHelper.ShowForegroundMessageBox("Another FreeSnip setup is running. Finish it before uninstalling.",
                    "FreeSnip Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            logger = await DeploymentLogger.CreateAsync(logPath, "UNINSTALL", ct).ConfigureAwait(false);
            progress = await DeploymentProgress.CreateAsync("FreeSnip Uninstaller", logPath);

            await ReportAsync(progress, logger, 5, "UNINSTALL", "INIT", "Starting scorched-earth cleanup...", ct).ConfigureAwait(false);

            if (!await WaitForApplicationsToCloseAsync(progress, ct).ConfigureAwait(false)) return 0;
            StartupTaskHelper.RequireInstalledApplicationsClosed();

            await PerformFullHostCleanupAsync(progress, logger, "Uninstall", 10, 90, requireZeroFootprint: true, purgeUserArtifacts: true, ct).ConfigureAwait(false);

            await ReportAsync(progress, logger, 95, "UNINSTALL", "VERIFYING", "Confirming system state...", ct).ConfigureAwait(false);
            var residue = await CollectResidualFootprintAsync(logger, ct).ConfigureAwait(false);
            int rebootPending = Volatile.Read(ref _pendingRebootDeletes);

            foreach (string item in residue)
                await ReportAsync(progress, logger, 97, "UNINSTALL", "REMAINING", item, ct).ConfigureAwait(false);

            string status;
            string detail;
            if (residue.Count == 0 && rebootPending == 0)
            {
                status = "SUCCESS";
                detail = "Verified: every installed component was removed.";
            }
            else if (rebootPending > 0)
            {
                status = "PENDING REBOOT";
                detail = $"{rebootPending} locked item(s) are scheduled for deletion on the next restart." +
                         (residue.Count > 0 ? $" {residue.Count} other item(s) still present - see the log." : string.Empty);
            }
            else
            {
                status = "INCOMPLETE";
                detail = $"{residue.Count} item(s) could not be removed. The list is above and in the log.";
            }
            
            await ReportAsync(progress, logger, 100, "UNINSTALL", status, detail, ct).ConfigureAwait(false);
            await AwaitUserAcknowledgementAsync(progress, logger, detail + " Click Finish to close.", ct).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            BootstrapDebug.Log("Worker FATAL: " + ex);
            await ReportAsync(progress, logger, 100, "FAILURE", "ERROR", ex.Message, ct, ex).ConfigureAwait(false);
            await AwaitUserAcknowledgementAsync(progress, logger, "Uninstall failed: " + ex.Message, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    uninstallGate.Release();
                }
                catch (SemaphoreFullException) { }
            }
            progress?.Dispose();
            if (logger != null) await logger.DisposeAsync().ConfigureAwait(false);
            if (lockAcquired)
            {
                QueueSelfCleanup(DeploymentFootprint.DeploymentTempRoot);
            }
        }
    }

    private static async Task PerformFullHostCleanupAsync(DeploymentProgress progress, DeploymentLogger logger, string op, int start, int end, bool requireZeroFootprint, bool purgeUserArtifacts, CancellationToken ct)
    {
        Interlocked.Exchange(ref _pendingRebootDeletes, 0);
        await logger.LogAsync("CLEANUP", "START", $"Performing Scorched Earth for: {op}", ct).ConfigureAwait(false);

        await ReportAsync(progress, logger, start + 10, "CLEANUP", "TASKS", "Removing triggers...", ct).ConfigureAwait(false);
        await RunHiddenProcessAsync("schtasks.exe", $"/Delete /TN \"{DeploymentFootprint.ScheduledTaskName}\" /F", 5000, logger, ct).ConfigureAwait(false);
        var targets = DeploymentFootprint.GetDirectoryPurgeTargets(includeInstallFolder: true).ToList();
        var allowedRoots = new[]
        {
            DeploymentFootprint.InstallFolder,
            DeploymentFootprint.ProgramDataFolder,
            DeploymentFootprint.RoamingAppDataFolder,
            DeploymentFootprint.LocalAppDataFolder,
            DeploymentFootprint.TempAppFolder,
            DeploymentFootprint.DeploymentTempRoot
        };
        targets.RemoveAll(t => !allowedRoots.Any(root =>
            t.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(t, root, StringComparison.OrdinalIgnoreCase)));
        if (!purgeUserArtifacts)
        {
            targets.RemoveAll(t =>
                string.Equals(t, DeploymentFootprint.RoamingAppDataFolder, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, DeploymentFootprint.LocalAppDataFolder, StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(DeploymentFootprint.RoamingAppDataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith(DeploymentFootprint.LocalAppDataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        foreach (string skipped in DeploymentFootprint.GetDirectoryPurgeTargets(includeInstallFolder: true).Except(targets, StringComparer.OrdinalIgnoreCase))
        {
            await logger.LogAsync("FILESYSTEM", "SKIP_GUARD", $"Refusing to purge outside owned roots: {skipped}", ct).ConfigureAwait(false);
        }
        for (int i = 0; i < targets.Count; i++)
        {
            int p = start + 15 + (int)((end - start - 40) * (i / (double)targets.Count));
            await ReportAsync(progress, logger, p, "CLEANUP", "FILESYSTEM", $"Purging: {targets[i]}", ct).ConfigureAwait(false);
            await PurgeDirectoryRecursiveAsync(targets[i], logger, ct).ConfigureAwait(false);
        }

        await ReportAsync(progress, logger, end - 20, "CLEANUP", "REGISTRY", "Scrubbing all hives...", ct).ConfigureAwait(false);
        await DeleteRegistryFootprintAsync(logger, ct).ConfigureAwait(false);

        if (!string.Equals(op, "Pre-Install Cleanup", StringComparison.OrdinalIgnoreCase))
        {
            await DeleteFileWithRetryAsync(DeploymentFootprint.TempInstallationLogPath, logger, ct).ConfigureAwait(false);
        }

        await ReportAsync(progress, logger, end - 10, "CLEANUP", "SHELL", "Cleaning links...", ct).ConfigureAwait(false);
        await DeleteKnownShortcutsAsync(logger, ct).ConfigureAwait(false);

        if (purgeUserArtifacts)
        {
            await ReportAsync(progress, logger, end - 5, "CLEANUP", "USER", "Removing temporary artifacts...", ct).ConfigureAwait(false);
            await PurgeUserGeneratedArtifactsAsync(logger, ct).ConfigureAwait(false);
        }

        if (requireZeroFootprint)
        {
            var residue = await CollectResidualFootprintAsync(logger, ct).ConfigureAwait(false);
            foreach (string item in residue)
                await ReportAsync(progress, logger, end, "CLEANUP", "REMAINING", item, ct).ConfigureAwait(false);
        }
    }

    private static async Task PurgeDirectoryRecursiveAsync(string dir, DeploymentLogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            await logger.LogAsync("FILESYSTEM", "PURGE_BEGIN", dir, ct).ConfigureAwait(false);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            }
            catch (Exception enumEx)
            {
                await logger.LogAsync("FILESYSTEM", "ENUM_FAIL", $"{dir} :: {enumEx.Message}", ct).ConfigureAwait(false);
                files = Array.Empty<string>();
            }

            int deleted = 0;
            var throttler = new SemaphoreSlim(20);
            var tasks = new List<Task>(files.Length);
            try
            {
                foreach (string file in files)
                {
                    await throttler.WaitAsync(ct).ConfigureAwait(false);
                    string capture = file;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (await DeleteFileWithRetryAsync(capture, logger, ct).ConfigureAwait(false)) Interlocked.Increment(ref deleted);
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                throttler.Dispose();
            }

            await logger.LogAsync("FILESYSTEM", "FILES_REMOVED", $"{dir} :: {deleted}/{files.Length}", ct).ConfigureAwait(false);

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
            }
            catch (Exception enumEx)
            {
                await logger.LogAsync("FILESYSTEM", "ENUM_FAIL", $"{dir} :: {enumEx.Message}", ct).ConfigureAwait(false);
                subDirs = Array.Empty<string>();
            }

            foreach (string sub in subDirs.OrderByDescending(d => d.Length))
            {
                await DeleteDirectoryWithRetryAsync(sub, logger, ct).ConfigureAwait(false);
            }

            await DeleteDirectoryWithRetryAsync(dir, logger, ct).ConfigureAwait(false);
            await logger.LogAsync("FILESYSTEM", "PURGE_END", dir, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await logger.LogAsync("FILESYSTEM", "ERROR", $"Failed to purge {dir}: {ex.Message}", ct).ConfigureAwait(false);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, DeploymentLogger logger, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return;

        for (int i = 0; i < DeleteRetries; i++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (Exception ex)
            {
                await logger.LogAsync("FILESYSTEM", "SET_ATTR_FAIL", $"{path} :: {ex.Message}", ct).ConfigureAwait(false);
                LogSwallowed("DeleteDirectoryWithRetryAsync", path, ex);
            }

            try
            {
                Directory.Delete(path, true);
                await logger.LogAsync("FILESYSTEM", "DELETE_DIR", path, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (i < DeleteRetries - 1)
                {
                    await logger.LogAsync("FILESYSTEM", "DELETE_DIR_RETRY", $"{path} :: {ex.Message}", ct).ConfigureAwait(false);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                await ScheduleDirectoryForRebootDeleteAsync(path, logger, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task ScheduleDirectoryForRebootDeleteAsync(string path, DeploymentLogger logger, CancellationToken ct)
    {
        try
        {
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!MoveFileEx(file, null, MoveFileFlags.DelayUntilReboot)) continue;
                Interlocked.Increment(ref _pendingRebootDeletes);
                await logger.LogAsync("FILESYSTEM", "REBOOT_DELETE", file, ct).ConfigureAwait(false);
            }

            foreach (string sub in Directory.GetDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (!MoveFileEx(sub, null, MoveFileFlags.DelayUntilReboot)) continue;
                Interlocked.Increment(ref _pendingRebootDeletes);
                await logger.LogAsync("FILESYSTEM", "REBOOT_DELETE_DIR", sub, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await logger.LogAsync("FILESYSTEM", "REBOOT_SCAN_FAIL", $"{path} :: {ex.Message}", ct).ConfigureAwait(false);
        }

        if (MoveFileEx(path, null, MoveFileFlags.DelayUntilReboot))
        {
            Interlocked.Increment(ref _pendingRebootDeletes);
            await logger.LogAsync("FILESYSTEM", "REBOOT_DELETE_DIR", path, ct).ConfigureAwait(false);
        }
        else
        {
            await logger.LogAsync("FILESYSTEM", "DELETE_DIR_FAIL", $"{path} :: could not be removed or scheduled", ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DeleteFileWithRetryAsync(string path, DeploymentLogger logger, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;

        for (int i = 0; i < DeleteRetries; i++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                await logger.LogAsync("FILESYSTEM", "DELETE", path, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                if (i < DeleteRetries - 1)
                {
                    await logger.LogAsync("FILESYSTEM", "DELETE_RETRY", $"{path} :: {ex.Message}", ct).ConfigureAwait(false);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                if (MoveFileEx(path, null, MoveFileFlags.DelayUntilReboot))
                {
                    Interlocked.Increment(ref _pendingRebootDeletes);
                    await logger.LogAsync("FILESYSTEM", "REBOOT_DELETE", path, ct).ConfigureAwait(false);
                }
                else
                {
                    await logger.LogAsync("FILESYSTEM", "DELETE_FAIL", $"{path} :: {ex.Message}", ct).ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    private static async Task DeleteRegistryFootprintAsync(DeploymentLogger logger, CancellationToken ct)
    {
        foreach (var target in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
            await DeleteSubKeyTreeAsync(target.Hive, target.View, target.SubKeyPath, logger, ct).ConfigureAwait(false);

        foreach (var target in DeploymentFootprint.GetAppRegistryPurgeTargets())
            await DeleteSubKeyTreeAsync(target.Hive, target.View, target.Path, logger, ct).ConfigureAwait(false);

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var mui in DeploymentFootprint.MuiCacheRelativePaths)
                {
                    try
                    {
                        using var key = baseKey.OpenSubKey(mui, true);
                        if (key == null) continue;
                        foreach (var name in key.GetValueNames())
                        {
                            if (name.Contains(DeploymentFootprint.AppName, StringComparison.OrdinalIgnoreCase))
                            {
                                try 
                                { 
                                    key.DeleteValue(name, false); 
                                    await logger.LogAsync("REGISTRY", "MUI_PURGE", $"{hive}\\{mui}\\{name}", ct).ConfigureAwait(false); 
                                } 
                                catch (Exception ex)
                                {
                                    await logger.LogAsync("REGISTRY", "MUI_PURGE_FAIL", $"{hive}\\{mui}\\{name} :: {ex.Message}", ct).ConfigureAwait(false);
                                    LogSwallowed("DeleteMuiCacheValuesAsync", $"{hive}\\{mui}\\{name}", ex);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await logger.LogAsync("REGISTRY", "MUI_KEY_FAIL", $"{hive}\\{mui} :: {ex.Message}", ct).ConfigureAwait(false);
                        LogSwallowed("DeleteMuiCacheValuesAsync", $"{hive}\\{mui}", ex);
                    }
                }
            }
        }

        await DeleteRunRegistryValuesAsync(logger, ct).ConfigureAwait(false);
        await DeleteFileAssociationsRegistryAsync(logger, ct).ConfigureAwait(false);
        await ScrubExplorerFileExtsAsync(logger, ct).ConfigureAwait(false);
        await ScrubPushNotificationsAsync(logger, ct).ConfigureAwait(false);
        await ScrubAppCompatFlagsAsync(logger, ct).ConfigureAwait(false);
        await ScrubTilePropertiesAsync(logger, ct).ConfigureAwait(false);
    }

    private static async Task DeleteRunRegistryValuesAsync(DeploymentLogger logger, CancellationToken ct)
    {
        string installFolder = StartupTaskHelper.InstallFolder.TrimEnd(Path.DirectorySeparatorChar);

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (string runPath in DeploymentFootprint.RunKeyRelativePaths)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(runPath, true);
                        if (key == null) continue;

                        foreach (string name in key.GetValueNames())
                        {
                            bool nameMatch = DeploymentFootprint.RunValueNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
                            bool dataMatch = false;
                            if (!nameMatch)
                            {
                                string data = key.GetValue(name)?.ToString() ?? string.Empty;
                                dataMatch = data.IndexOf(installFolder, StringComparison.OrdinalIgnoreCase) >= 0;
                            }

                            if (!nameMatch && !dataMatch) continue;

                            try
                            {
                                key.DeleteValue(name, false);
                                await logger.LogAsync("REGISTRY", "DELETE_RUN_VALUE", $"{hive}\\{runPath}\\{name}", ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                await logger.LogAsync("REGISTRY", "DELETE_RUN_VALUE_FAIL", $"{hive}\\{runPath}\\{name} :: {ex.Message}", ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await logger.LogAsync("REGISTRY", "RUN_KEY_SKIP", $"{hive}\\{runPath} :: {ex.Message}", ct).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static async Task DeleteSubKeyTreeAsync(RegistryHive hive, RegistryView view, string path, DeploymentLogger logger, CancellationToken ct)
    {
        try 
        { 
            using var root = RegistryKey.OpenBaseKey(hive, view); 
            root.DeleteSubKeyTree(path, false); 
            await logger.LogAsync("REGISTRY", "DELETE_KEY", $"{hive}\\{path}", ct).ConfigureAwait(false); 
        } 
        catch (Exception ex)
        {
            await logger.LogAsync("REGISTRY", "DELETE_KEY_FAIL", $"{hive}\\{path} :: {ex.Message}", ct).ConfigureAwait(false);
            LogSwallowed("DeleteSubKeyTreeAsync", $"{hive}\\{path}", ex);
        }
    }

    private static readonly HashSet<string> ConflictingProcessNames = new(StringComparer.Ordinal)
    {
        "greenshot", "lightweight_greenshot", "lightshot", "snagit", "snagiteditor",
        "sharex", "screenclippinghost", "gyazo", "fscapture", "picpick", "jing", "skitch",
        "droplr", "cloudapp", "monosnap", "screenpresso", "tinytake", "ashampoosnap",
        "movaviscreenrecorder", "bandicam", "camtasiastudio", "camtasia", "fraps", "obs64", "obs32"
    };

    private static readonly HashSet<string> ConflictingDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Greenshot", "Lightshot", "Snagit", "ShareX", "Snipping Tool", "ScreenClippingHost",
        "Gyazo", "FastStone Capture", "PicPick", "Jing", "Skitch", "Droplr", "CloudApp",
        "Monosnap", "Screenpresso", "TinyTake", "Ashampoo Snap", "Movavi Screen Recorder",
        "Bandicam", "Camtasia Studio", "Fraps", "OBS Studio", "Snagit Editor"
    };

    internal static string MatchConflictingProcessName(IEnumerable<string> processNames)
    {
        foreach (string name in processNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (ConflictingProcessNames.Contains(name.ToLowerInvariant())) return name;
        }
        return null;
    }

    internal static string MatchConflictingDisplayName(IEnumerable<string> displayNames)
    {
        foreach (string name in displayNames)
        {
            string trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (ConflictingDisplayNames.Contains(trimmed)) return trimmed;
        }
        return null;
    }

    private static string DetectConflictingSoftware()
    {
        try
        {
            var processNames = new List<string>();
            foreach (var p in System.Diagnostics.Process.GetProcesses()) { processNames.Add(p.ProcessName); }
            string byProcess = MatchConflictingProcessName(processNames);
            if (byProcess != null) return byProcess;

            string[] keys = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            var displayNames = new List<string>();
            foreach (string keyPath in keys)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;
                foreach (var subkeyName in key.GetSubKeyNames())
                {
                    using var subkey = key.OpenSubKey(subkeyName);
                    displayNames.Add(subkey?.GetValue("DisplayName")?.ToString() ?? string.Empty);
                }
            }
            return MatchConflictingDisplayName(displayNames);
        }
        catch (Exception ex) { LogSwallowed("DetectConflictingSoftware", "scan", ex); return null; }
    }

    private static async Task InstallFreshAsync(DeploymentProgress progress, DeploymentLogger logger, CancellationToken ct)
    {
        string installFolder = StartupTaskHelper.InstallFolder;
        Directory.CreateDirectory(installFolder);

        if (PayloadExtractor.HasEmbeddedPayload())
        {
            await CopyFileAggressiveAsync(RuntimePathHelper.ExecutablePath, StartupTaskHelper.InstallPath, logger, ct).ConfigureAwait(false);
            await PayloadExtractor.ExtractToAsync(installFolder, ct).ConfigureAwait(false);
            await CopyFileAggressiveAsync(StartupTaskHelper.InstallPath, StartupTaskHelper.UninstallExePath, logger, ct).ConfigureAwait(false);
        }
        else
        {
            await CopyFileAggressiveAsync(RuntimePathHelper.ExecutablePath, StartupTaskHelper.InstallPath, logger, ct).ConfigureAwait(false);
            await CopyFileAggressiveAsync(RuntimePathHelper.ExecutablePath, StartupTaskHelper.UninstallExePath, logger, ct).ConfigureAwait(false);
        }

        await InitializeInstalledConfigurationAsync(logger, ct).ConfigureAwait(false);
        await WriteUninstallRegistryAsync(logger, ct).ConfigureAwait(false);
        await RegisterFileAssociationsAsync(logger, ct).ConfigureAwait(false);
        await CreateStartMenuShortcutAsync(logger, ct).ConfigureAwait(false);
        NotifyShellAssociationsChanged();
    }

    public static async Task<int> RunUninstallLauncherAsync(string[] args, CancellationToken ct)
    {
        try
        {
            if (StartupTaskHelper.IsRunningFromInstallPath())
            {
                BootstrapDebug.Log("Launcher: Relaunching from temp (install path detected).");
                return await RelaunchUninstallElevatedAsync(ct).ConfigureAwait(false);
            }

            if (StartupTaskHelper.IsElevated())
            {
                BootstrapDebug.Log("Launcher: Running worker directly (already elevated and in temp).");
                var workerArgs = args.ToList();
                if (!workerArgs.Contains("--cleanup-worker", StringComparer.OrdinalIgnoreCase))
                    workerArgs.Add("--cleanup-worker");
                return await RunUninstallAsync(workerArgs.ToArray(), ct).ConfigureAwait(false);
            }
            BootstrapDebug.Log("Launcher: Relaunching elevated.");
            return await RelaunchUninstallElevatedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BootstrapDebug.Log("Launcher FATAL: " + ex);
            StartupTaskHelper.ShowForegroundMessageBox("Uninstall could not start: " + ex.Message, "Uninstall Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task<int> RelaunchUninstallElevatedAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(SessionTempFolder);
        string src = RuntimePathHelper.ExecutablePath;
        string srcDir = Path.GetDirectoryName(src);
        string temp = Path.Combine(SessionTempFolder, "Uninstall.exe");
        
        BootstrapDebug.Log($"Relaunching: Copying {src} -> {temp}");
        File.Copy(src, temp, true);
        
        if (Directory.Exists(srcDir))
        {
            foreach (string dll in Directory.GetFiles(srcDir, "*.dll"))
            {
                try 
                { 
                    string dest = Path.Combine(SessionTempFolder, Path.GetFileName(dll));
                    File.Copy(dll, dest, true); 
                } 
                catch (Exception ex)
                {
                    LogSwallowed("RelaunchUninstallElevatedAsync", $"Copy {dll} -> {SessionTempFolder}", ex);
                }
            }
        }

        int pid = Process.GetCurrentProcess().Id;
        string newArgs = $"--uninstall --cleanup-worker {pid}";

        bool started = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = temp,
                Arguments = newArgs,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            Process.Start(psi);
            started = true;
        }
        catch (Exception ex)
        {
            BootstrapDebug.Log("Relaunch fallback to TryStartElevated: " + ex.Message);
            started = TryStartElevated(temp, newArgs);
        }

        if (!started) return 1;

        await Task.CompletedTask;
        return 0;
    }

    private static async Task<List<string>> CollectResidualFootprintAsync(DeploymentLogger logger, CancellationToken ct)
    {
        await Task.Delay(1000, ct).ConfigureAwait(false);
        var residue = new List<string>();

        foreach (string target in DeploymentFootprint.GetFullVerificationTargets())
        {
            if (!Directory.Exists(target)) continue;
            var survivors = EnumerateSurvivingEntries(target).ToList();
            if (survivors.Count == 0) continue;
            residue.Add($"Folder: {target} ({survivors.Count} item(s))");
            foreach (string survivor in survivors.Take(20))
                await logger.LogAsync("VERIFY", "RESIDUE_FILE", survivor, ct).ConfigureAwait(false);
        }

        foreach (var reg in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            using var baseKey = RegistryKey.OpenBaseKey(reg.Hive, reg.View);
            using var key = baseKey.OpenSubKey(reg.SubKeyPath);
            if (key != null) residue.Add($"Registry key: {reg.Hive}\\{reg.SubKeyPath}");
        }

        foreach (var reg in DeploymentFootprint.GetAppRegistryPurgeTargets())
        {
            using var baseKey = RegistryKey.OpenBaseKey(reg.Hive, reg.View);
            using var key = baseKey.OpenSubKey(reg.Path);
            if (key != null) residue.Add($"Registry key: {reg.Hive}\\{reg.Path}");
        }

        foreach (var reg in DeploymentFootprint.GetFileAssociationVerificationTargets())
        {
            using var baseKey = RegistryKey.OpenBaseKey(reg.Hive, reg.View);
            using var key = baseKey.OpenSubKey(reg.SubKeyPath);
            if (key != null) residue.Add($"File association: {reg.SubKeyPath}");
        }

        string installFolder = StartupTaskHelper.InstallFolder.TrimEnd(Path.DirectorySeparatorChar);
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (string runPath in DeploymentFootprint.RunKeyRelativePaths)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(runPath);
                        if (key == null) continue;
                        foreach (string name in key.GetValueNames())
                        {
                            bool nameMatch = DeploymentFootprint.RunValueNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
                            string data = key.GetValue(name)?.ToString() ?? string.Empty;
                            if (nameMatch || data.IndexOf(installFolder, StringComparison.OrdinalIgnoreCase) >= 0)
                                residue.Add($"Autostart value: {hive}\\{runPath}\\{name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        await logger.LogAsync("REGISTRY", "VERIFY_RUN_KEY_FAIL", $"{hive}\\{runPath} :: {ex.Message}", ct).ConfigureAwait(false);
                        LogSwallowed("GetVerificationTargets", $"{hive}\\{runPath}", ex);
                    }
                }
            }
        }

        foreach (string dir in DeploymentFootprint.GetShortcutSearchFolders())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string name in DeploymentFootprint.ShortcutFileNames)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) residue.Add($"Shortcut: {path}");
            }
        }

        if (await StartupTaskHelper.HasElevatedStartupTaskAsync().ConfigureAwait(false))
            residue.Add($"Scheduled task: {DeploymentFootprint.ScheduledTaskName}");

        foreach (string item in residue)
            await logger.LogAsync("VERIFY", "RESIDUE", item, ct).ConfigureAwait(false);

        await logger.LogAsync("VERIFY", residue.Count == 0 ? "CLEAN" : "INCOMPLETE", $"{residue.Count} residual item(s)", ct).ConfigureAwait(false);
        return residue;
    }

    private static IEnumerable<string> EnumerateSurvivingEntries(string root)
    {
        string live = SessionTempFolder.TrimEnd(Path.DirectorySeparatorChar);
        string lifecycle = DeploymentFootprint.DeploymentTempRoot.TrimEnd(Path.DirectorySeparatorChar);
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (string entry in entries)
        {
            if (entry.StartsWith(live, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.StartsWith(lifecycle, StringComparison.OrdinalIgnoreCase)) continue;
            yield return entry;
        }
    }

    private static async Task WriteUninstallRegistryAsync(DeploymentLogger logger, CancellationToken ct)
    {
        foreach (var purgeTarget in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            await DeleteSubKeyTreeAsync(purgeTarget.Hive, purgeTarget.View, purgeTarget.SubKeyPath, logger, ct).ConfigureAwait(false);
        }

        var target = DeploymentFootprint.GetCanonicalUninstallRegistryTarget();
        using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
        using var key = baseKey.CreateSubKey(target.SubKeyPath);
        
        key.SetValue("DisplayName", DeploymentFootprint.DisplayName);
        key.SetValue("UninstallString", $"\"{StartupTaskHelper.UninstallExePath}\" --uninstall");
        key.SetValue("DisplayIcon", StartupTaskHelper.InstallPath);
        key.SetValue("InstallLocation", StartupTaskHelper.InstallFolder);
        key.SetValue("DisplayVersion", RuntimePathHelper.ProductVersion);
        key.SetValue("Publisher", DeploymentFootprint.DisplayName);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        
        await logger.LogAsync("REGISTRY", "UNINSTALL_REGISTERED", target.SubKeyPath, ct).ConfigureAwait(false);
    }

    private static async Task RegisterFileAssociationsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        string cmd = $"\"{StartupTaskHelper.InstallPath}\" \"%1\"";
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Classes", true);
        if (classes == null) return;

        using (var progId = classes.CreateSubKey(DeploymentFootprint.ProgId))
        {
            progId.SetValue("", "FreeSnip Image");
            progId.CreateSubKey(@"shell\open\command").SetValue("", cmd);
        }

        foreach (string ext in DeploymentFootprint.ImageExtensions)
        {
            using (var openWith = classes.CreateSubKey(ext + @"\OpenWithProgids", true)) openWith.SetValue(DeploymentFootprint.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var shell = classes.CreateSubKey(ext + @"\shell\" + DeploymentFootprint.OpenWithShellName + @"\command", true)) shell.SetValue("", cmd);
        }
        await logger.LogAsync("REGISTRY", "FILE_ASSOC_CREATED", "ProgId and extensions registered.", ct).ConfigureAwait(false);
    }

    private static async Task DeleteFileAssociationsRegistryAsync(DeploymentLogger logger, CancellationToken ct)
    {
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Classes", true);
        if (classes == null) return;

        try 
        { 
            classes.DeleteSubKeyTree(DeploymentFootprint.ProgId, false); 
        } 
        catch (Exception ex)
        { 
            await logger.LogAsync("REGISTRY", "DELETE_PROGID_FAIL", $"{DeploymentFootprint.ProgId} :: {ex.Message}", ct).ConfigureAwait(false);
            LogSwallowed("DeleteFileAssociationsRegistryAsync", DeploymentFootprint.ProgId, ex);
        }

        foreach (string ext in DeploymentFootprint.ImageExtensions)
        {
            try 
            { 
                using var openWith = classes.OpenSubKey(ext + @"\OpenWithProgids", true); 
                openWith?.DeleteValue(DeploymentFootprint.ProgId, false); 
            } 
            catch (Exception ex)
            { 
                await logger.LogAsync("REGISTRY", "DELETE_OPENWITH_FAIL", $"{ext} :: {ex.Message}", ct).ConfigureAwait(false);
                LogSwallowed("DeleteFileAssociationsRegistryAsync", $"{ext}\\OpenWithProgids", ex);
            }

            try 
            { 
                classes.DeleteSubKeyTree(ext + @"\shell\" + DeploymentFootprint.OpenWithShellName, false); 
            } 
            catch (Exception ex)
            { 
                await logger.LogAsync("REGISTRY", "DELETE_SHELL_FAIL", $"{ext} :: {ex.Message}", ct).ConfigureAwait(false);
                LogSwallowed("DeleteFileAssociationsRegistryAsync", $"{ext}\\shell\\{DeploymentFootprint.OpenWithShellName}", ex);
            }
        }
    }

    private static async Task DeleteKnownShortcutsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        foreach (string dir in DeploymentFootprint.GetShortcutSearchFolders())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string name in DeploymentFootprint.ShortcutFileNames)
            {
                string path = Path.Combine(dir, name);
                await DeleteFileWithRetryAsync(path, logger, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task PurgeUserGeneratedArtifactsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        foreach (string pattern in DeploymentFootprint.GetUserArtifactPatterns())
        {
            string dir = Path.GetDirectoryName(pattern);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, Path.GetFileName(pattern)))
                    await DeleteFileWithRetryAsync(file, logger, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await logger.LogAsync("FILESYSTEM", "PURGE_ARTIFACTS_FAIL", $"{dir}\\{Path.GetFileName(pattern)} :: {ex.Message}", ct).ConfigureAwait(false);
                LogSwallowed("PurgeUserGeneratedArtifactsAsync", pattern, ex);
            }
        }
    }

    private static async Task InitializeInstalledConfigurationAsync(DeploymentLogger logger, CancellationToken ct)
    {
        Directory.CreateDirectory(StartupTaskHelper.ConfigurationFolder);
        await Task.Run(() => IniConfigurationDeployer.EnsureUserConfiguration(StartupTaskHelper.ConfigurationFolder), ct).ConfigureAwait(false);
    }

    private static async Task CreateStartMenuShortcutAsync(DeploymentLogger logger, CancellationToken ct)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "FreeSnip.lnk");
        await ShellLinkWriter.CreateAsync(path, StartupTaskHelper.InstallPath, StartupTaskHelper.InstallFolder, StartupTaskHelper.InstallPath + ",0", "FreeSnip", ct).ConfigureAwait(false);
        await logger.LogAsync("SHELL", "SHORTCUT", path, ct).ConfigureAwait(false);
    }

    private static async Task CopyFileAggressiveAsync(string src, string dest, DeploymentLogger logger, CancellationToken ct)
    {
        string destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        const int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(dest))
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                    File.Delete(dest);
                }
                File.Copy(src, dest, true);
                File.SetAttributes(dest, FileAttributes.Normal);
                await logger.LogAsync("FILESYSTEM", "COPY", dest, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    await logger.LogAsync("FILESYSTEM", "COPY_FAIL", $"{dest} :: {ex.Message}", ct, ex).ConfigureAwait(false);
                    throw;
                }

                await logger.LogAsync("FILESYSTEM", "COPY_RETRY", $"Attempt {attempt} for {dest}: {ex.Message}", ct).ConfigureAwait(false);
                StartupTaskHelper.RequireInstalledApplicationsClosed();
                await Task.Delay(250 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task LaunchInstalledApplicationAsync()
    {
        const int launchAttempts = 3;

        for (int attempt = 1; attempt <= launchAttempts; attempt++)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = StartupTaskHelper.InstallPath,
                    WorkingDirectory = StartupTaskHelper.InstallFolder,
                    UseShellExecute = true
                });

                await Task.Delay(1200).ConfigureAwait(false);

                if (IsInstalledApplicationRunning())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                LogSwallowed("LaunchInstalledApplicationAsync", $"attempt {attempt}", ex);
            }

            await Task.Delay(800).ConfigureAwait(false);
        }
        throw new IOException("The installed application did not start. Any settings recovery copy has been kept.");
    }

    private static bool IsInstalledApplicationRunning()
    {
        try
        {
            string[] names = { "FreeSnip", "freesnip", "FreeSnip_tesseract", "freesnip_tesseract" };
            foreach (var name in names)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = process.MainModule?.FileName;
                        if (string.Equals(path, StartupTaskHelper.InstallPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSwallowed("IsInstalledApplicationRunning", $"process {process.Id}", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("IsInstalledApplicationRunning", "GetProcessesByName", ex);
        }

        return false;
    }

    private static void StartElevated(string exe, string args) => TryStartElevated(exe, args);
    private static bool TryStartElevated(string exe, string args) { try { Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true, Verb = "runas" }); return true; } catch (Exception ex) { LogSwallowed("TryStartElevated", $"{exe} {args}", ex); return false; } }
    private static int ParseParentPid(string[] args) => args.Select(a => int.TryParse(a, out int p) ? p : 0).FirstOrDefault(p => p > 0);
    private static async Task WaitForParentExitAsync(int pid, CancellationToken ct) { try { using var p = Process.GetProcessById(pid); await p.WaitForExitAsync(ct); } catch (Exception ex) { LogSwallowed("WaitForParentExitAsync", $"pid {pid}", ex); } }
    private static void NotifyShellAssociationsChanged() => SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

    private static void QueueSelfCleanup(string dir)
    {
        Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"", CreateNoWindow = true, UseShellExecute = false });
    }

    private static bool DetectExistingInstallation()
    {
        try
        {
            foreach (string dir in DeploymentFootprint.GetVerificationTargets())
            {
                if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return true;
            }

            foreach (var reg in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
            {
                using var baseKey = RegistryKey.OpenBaseKey(reg.Hive, reg.View);
                using var key = baseKey?.OpenSubKey(reg.SubKeyPath);
                if (key != null) return true;
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("DetectExistingInstallation", "scan", ex);
        }

        return false;
    }

    private static string[] GetSettingsCandidates() => StartupTaskHelper.GetSettingsCandidates();

    private static async Task<string> BackupUserSettingsAsync(DeploymentLogger logger, CancellationToken ct)
    {
        string backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSnipUpgradeBackups");
        string folder = await UpgradeSettingsBackup.CreateAsync(GetSettingsCandidates(), backupRoot, ct).ConfigureAwait(false);
        await logger.LogAsync("UPGRADE", "BACKUP", folder ?? "No existing settings file", ct).ConfigureAwait(false);
        return folder;
    }

    private static async Task RestoreUserSettingsAsync(string folder, DeploymentLogger logger, CancellationToken ct)
    {
        await UpgradeSettingsBackup.RestoreAsync(folder, GetSettingsCandidates(), ct).ConfigureAwait(false);
        string primaryIni = Path.Combine(StartupTaskHelper.ConfigurationFolder, "freesnip.ini");
        if (!File.Exists(primaryIni))
        {
            foreach (var cand in GetSettingsCandidates())
            {
                if (File.Exists(cand) && !string.Equals(cand, primaryIni, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(StartupTaskHelper.ConfigurationFolder);
                        string content = await File.ReadAllTextAsync(cand, ct).ConfigureAwait(false);
                        content = content.Replace("[SnapVox]", "[Core]", StringComparison.OrdinalIgnoreCase);
                        await File.WriteAllTextAsync(primaryIni, content, Encoding.UTF8, ct).ConfigureAwait(false);
                        await logger.LogAsync("UPGRADE", "MIGRATE", $"Migrated '{cand}' to '{primaryIni}'", ct).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogSwallowed("RestoreUserSettingsAsync", "migrate", ex);
                    }
                }
            }
        }
        await logger.LogAsync("UPGRADE", "RESTORED", "Settings verified from " + folder, ct).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForApplicationsToCloseAsync(DeploymentProgress progress, CancellationToken ct)
    {
        while (StartupTaskHelper.FindRunningInstalledProcesses().Length != 0)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ShowBlockingPromptAsync(progress,
                "FreeSnip is still running. Save your open pictures and exit FreeSnip using its tray menu.\r\n\r\n" +
                "Choose Retry after closing it, or Cancel to leave setup. Setup will not force-close your work.",
                "Save your work before continuing", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information).ConfigureAwait(false);
            if (result != DialogResult.Retry) return false;
        }
        return true;
    }

    private static void CleanupSettingsBackup(string backupFolder)
    {
        try
        {
            if (!string.IsNullOrEmpty(backupFolder) && Directory.Exists(backupFolder))
            {
                Directory.Delete(backupFolder, true);
                string parent = Path.GetDirectoryName(backupFolder);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent, false);
                }
            }
        }
        catch (Exception ex)
        {
            LogSwallowed("CleanupSettingsBackup", backupFolder, ex);
        }
    }

    private static async Task AwaitUserAcknowledgementAsync(DeploymentProgress progress, DeploymentLogger logger, string finalStatus, CancellationToken ct)
    {
        if (progress == null) return;
        try
        {
            await progress.WaitForAcknowledgementAsync(finalStatus, ct).ConfigureAwait(false);
            if (logger != null) await logger.LogAsync("UI", "ACKNOWLEDGED", finalStatus, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (logger != null) await logger.LogAsync("UI", "ACK_TIMEOUT", "Result window closed automatically after 30 minutes.", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.LogAsync("UI", "ACK_ERROR", ex.Message, ct).ConfigureAwait(false);
        }
    }

    private static async Task ReportAsync(DeploymentProgress p, DeploymentLogger l, int pct, string phase, string status, string detail, CancellationToken ct, Exception ex = null)
    {
        p?.Update(pct, $"[{phase}] {status}: {detail}");
        if (l != null) await l.LogAsync(phase, status, detail, ct, ex).ConfigureAwait(false);
    }

    private static async Task<DialogResult> ShowBlockingPromptAsync(DeploymentProgress progress, string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        progress?.SuppressTopmost();
        IntPtr hwnd = IntPtr.Zero;
        if (progress != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                hwnd = progress.GetWindowHandle();
            });
        }
        try
        {
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            return StartupTaskHelper.ShowForegroundMessageBox(message, title, buttons, icon, hwnd);
        }
        finally
        {
            progress?.RestoreTopmost();
        }
    }

    private static async Task<int> RunHiddenProcessAsync(string exe, string args, int timeout, DeploymentLogger logger, CancellationToken ct)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true } };
        p.Start();
        await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(timeout), ct);
        return p.ExitCode;
    }

    private static async Task RelaunchInstallFromTempAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(SessionTempFolder);
        string temp = Path.Combine(SessionTempFolder, "Setup.exe");
        File.Copy(RuntimePathHelper.ExecutablePath, temp, true);

        string extraArgs = "";
        string[] currentArgs = Environment.GetCommandLineArgs();
        if (currentArgs.Any(a => a.Equals("--auto-update", StringComparison.OrdinalIgnoreCase)))
            extraArgs += " --auto-update";
        for (int i = 0; i < currentArgs.Length; i++)
        {
            if (currentArgs[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < currentArgs.Length)
            {
                extraArgs += $" --wait-pid {currentArgs[i + 1]}";
            }
        }

        Process.Start(new ProcessStartInfo { FileName = temp, Arguments = "--install --install-worker" + extraArgs, UseShellExecute = true, Verb = "runas" });
        await Task.CompletedTask;
    }

    internal sealed class DeploymentProgress : IDisposable
    {
        private static readonly TimeSpan AcknowledgementBackstop = TimeSpan.FromMinutes(30);

        private freesnip.forms.DeploymentProgressWindow _window;
        private DeploymentProgress() {}

        public static Task<DeploymentProgress> CreateAsync(string title, string log)
        {
            var tcs = new TaskCompletionSource<DeploymentProgress>();
            Dispatcher.UIThread.Post(() => {
                var dp = new DeploymentProgress();
                dp._window = new freesnip.forms.DeploymentProgressWindow(title, log);
                dp._window.Show();
                tcs.SetResult(dp);
            });
            return tcs.Task;
        }
        public void Update(int pct, string status) 
        { 
            Dispatcher.UIThread.Post(() => {
                _window?.UpdateProgress(pct); 
                _window?.UpdateStatus(status); 
            });
        }

        public Task WaitForAcknowledgementAsync(string finalStatus, CancellationToken ct)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                var window = _window;
                if (window == null) { gate.TrySetResult(true); return; }
                window.EnableFinish(finalStatus);
                window.Acknowledged.ContinueWith(_ => gate.TrySetResult(true), TaskScheduler.Default);
            });

            return gate.Task.WaitAsync(AcknowledgementBackstop, ct);
        }

        public void SuppressTopmost()
        {
            Dispatcher.UIThread.Post(() => {
                try { if (_window != null) _window.Topmost = false; } catch (Exception ex) { LogSwallowed("DeploymentProgress.SuppressTopmost", "Topmost", ex); }
            });
        }
        public void RestoreTopmost()
        {
            Dispatcher.UIThread.Post(() => {
                try { if (_window != null) { _window.Topmost = false; _window.Activate(); } } catch (Exception ex) { LogSwallowed("DeploymentProgress.RestoreTopmost", "Activate", ex); }
            });
        }
        public IntPtr GetWindowHandle()
        {
            try
            {
                return _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LogSwallowed("DeploymentProgress.GetWindowHandle", "TryGetPlatformHandle", ex);
                return IntPtr.Zero;
            }
        }
        public void Dispose() 
        { 
            Dispatcher.UIThread.Post(() => {
                try { _window?.Close(); } catch (Exception ex) { LogSwallowed("DeploymentProgress.Dispose", "Close", ex); }
            }); 
        }
    }

    internal sealed class DeploymentLogger : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private DeploymentLogger(StreamWriter sw) { _writer = sw; }
        public static async Task<DeploymentLogger> CreateAsync(string path, string session, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
                await sw.WriteLineAsync($"\n=== {session} {DateTime.Now:O} PID={Environment.ProcessId} ===").ConfigureAwait(false);
                return new DeploymentLogger(sw);
            }
            catch (Exception ex)
            {
                LogSwallowed("DeploymentLogger.CreateAsync", path, ex);
                var sw = new StreamWriter(Stream.Null, Encoding.UTF8) { AutoFlush = true };
                return new DeploymentLogger(sw);
            }
        }
        public async Task LogAsync(string phase, string action, string detail, CancellationToken ct, Exception ex = null)
        {
            await _writer.WriteLineAsync($"{DateTime.Now:HH:mm:ss.fff}|{phase}|{action}|{detail}{(ex != null ? "|" + ex : "")}").ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync() { _writer.Dispose(); await ValueTask.CompletedTask; }
    }
}
