using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core.AvaloniaShims;
using Microsoft.Win32;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using log4net;
using System.Linq;

namespace freesnip.helpers;

public static class StartupTaskHelper
{
    private static ILog Log => LogHelper.IsInitialized ? freesnip.foundation.core.LogHelper.GetLogger(typeof(StartupTaskHelper)) : null;
    public const string ScheduledTaskName = "FreeSnip";
    private const string ConfigureAdminStartupArgument = "--configure-admin-startup";
    private const string RemoveAdminStartupArgument = "--remove-admin-startup";
    private const string StartupUserSidArgument = "--startup-user-sid";

    public static string InstallFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FreeSnip");
    public static string ConfigurationFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSnip");
    public static string InstallPath => Path.Combine(InstallFolder, "FreeSnip.exe");
    public static string UninstallExePath => Path.Combine(InstallFolder, "Uninstall.exe");

    private static void LogSuppressedException(string operation, Exception ex)
    {
        if (ex == null)
        {
            return;
        }

        Log?.Warn(operation, ex);
        ExecutionTrace.LogException("StartupTaskHelper." + operation, ex, string.Empty);
    }

    private static bool IsExpectedProcessInspectionException(Exception ex)
    {
        return ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException;
    }

    internal static bool? IsElevatedOverride { get; set; }

    public static bool IsElevated()
    {
        if (IsElevatedOverride.HasValue) return IsElevatedOverride.Value;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static Func<string, string, int, Task<int>> RunProcessHook { get; set; }
    internal static Func<string, string, int, Task<(int ExitCode, string Output, string Error)>> RunProcessWithOutputHook { get; set; }

    private static async Task<(int ExitCode, string Output, string Error)> RunHiddenProcessWithOutputAsync(string fileName, string arguments, int timeoutMilliseconds)
    {
        if (RunProcessWithOutputHook != null)
        {
            return await RunProcessWithOutputHook(fileName, arguments, timeoutMilliseconds).ConfigureAwait(false);
        }

        if (RunProcessHook != null)
        {
            int exit = await RunProcessHook(fileName, arguments, timeoutMilliseconds).ConfigureAwait(false);
            return (exit, string.Empty, string.Empty);
        }

        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!process.Start())
            {
                ExecutionTrace.LogEvent("StartupTaskHelper.RunHiddenProcess", "StartFailed", fileName + " " + arguments);
                return (-1, string.Empty, "Failed to start process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { process.Kill(); } catch (Exception ex) { LogSuppressedException("RunHiddenProcess.Kill", ex); }
                ExecutionTrace.LogEvent("StartupTaskHelper.RunHiddenProcess", "Timeout", fileName + " " + arguments);
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
                return (-2, string.Empty, "Timeout");
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            ExecutionTrace.LogEvent("StartupTaskHelper.RunHiddenProcess", "Exit", string.Format("{0};{1};{2};{3}", fileName, arguments, process.ExitCode, output + error));
            return (process.ExitCode, output, error);
        }
    }

    private static async Task<int> RunHiddenProcessAsync(string fileName, string arguments, int timeoutMilliseconds)
    {
        var (exitCode, _, _) = await RunHiddenProcessWithOutputAsync(fileName, arguments, timeoutMilliseconds).ConfigureAwait(false);
        return exitCode;
    }

    private static string GetStartupTaskExecutablePath()
    {
        if (File.Exists(InstallPath))
        {
            return InstallPath;
        }

        return RuntimePathHelper.ExecutablePath;
    }

    public static bool IsAdminStartupCommand(string[] args)
    {
        return args != null && args.Any(arg => arg.Equals(ConfigureAdminStartupArgument, StringComparison.OrdinalIgnoreCase) || arg.Equals(RemoveAdminStartupArgument, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAdminStartupCommandAsync(string[] args)
    {
        if (args == null)
        {
            return 1;
        }

        if (args.Any(arg => arg.Equals(ConfigureAdminStartupArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return await ConfigureElevatedStartupTaskInCurrentProcessAsync(null, GetStartupUserSidArgument(args)).ConfigureAwait(false) ? 0 : 1;
        }

        if (args.Any(arg => arg.Equals(RemoveAdminStartupArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return await DeleteElevatedStartupTaskInCurrentProcessAsync().ConfigureAwait(false) ? 0 : 1;
        }

        return 1;
    }

    private static string GetStartupUserSidArgument(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(StartupUserSidArgument, StringComparison.OrdinalIgnoreCase) && UpgradeUserContext.IsValidSid(args[i + 1]))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Returns the SID that owns the FreeSnip startup task. An explicitly supplied SID (the original upgrade user captured
    /// before UAC) always wins; the current process identity is used only for normal non-upgrade Settings UI operations.
    /// </summary>
    internal static string ResolveStartupTaskSid(string explicitUserSid)
    {
        if (!string.IsNullOrWhiteSpace(explicitUserSid))
        {
            if (!UpgradeUserContext.IsValidSid(explicitUserSid)) throw new ArgumentException("Invalid startup user SID.", nameof(explicitUserSid));
            return explicitUserSid;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Cannot identify the startup user.");
    }

    private static bool IsCurrentUserSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return true;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return string.Equals(identity.User?.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogSuppressedException("IsCurrentUserSid", ex);
            return false;
        }
    }

    private static async Task<bool> CreateElevatedStartupTaskAsync(string executablePath, bool elevated = true, string userSid = null)
    {
        string definitionPath = Path.Combine(Path.GetTempPath(), "FreeSnip", "Lifecycle", "Startup_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            string sid = ResolveStartupTaskSid(userSid);
            Directory.CreateDirectory(Path.GetDirectoryName(definitionPath));
            await File.WriteAllTextAsync(definitionPath, StartupTaskDefinition.Create(executablePath, sid, elevated)).ConfigureAwait(false);
            int exitCode = await RunHiddenProcessAsync("schtasks.exe",
                $"/Create /TN \"{ScheduledTaskName}\" /XML \"{definitionPath}\" /F", 15000).ConfigureAwait(false);
            if (exitCode != 0)
            {
                Log?.Error("Scheduled task registration failed with exit code " + exitCode);
                return false;
            }
            Log?.Info("Elevated startup registered with battery restrictions disabled for " + sid);
            LogInstallationElevationState("Scheduled task owner SID: " + sid + (string.IsNullOrWhiteSpace(userSid) ? " (current identity)" : " (explicit original user)"));
            return true;
        }
        catch (Exception ex)
        {
            LogSuppressedException("CreateElevatedStartupTask", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(definitionPath)) File.Delete(definitionPath); }
            catch (Exception ex) { LogSuppressedException("DeleteStartupDefinition", ex); }
        }
    }

    public static async Task EnsureBatteryRestrictionsDisabledAsync()
    {
        try
        {
            if (!await HasElevatedStartupTaskAsync().ConfigureAwait(false)) return;

            if (await HasBatteryOrPowerRestrictionsAsync().ConfigureAwait(false))
            {
                Log?.Info("Power or battery restrictions detected on scheduled task. Re-registering with explicit overrides...");
                string executable = GetStartupTaskExecutablePath();
                await ConfigureElevatedStartupTaskAsync(executable).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogSuppressedException("EnsureBatteryRestrictionsDisabled", ex);
        }
    }

    public static async Task<bool> HasBatteryOrPowerRestrictionsAsync()
    {
        try
        {
            var (exitCode, output, _) = await RunHiddenProcessWithOutputAsync("schtasks.exe", string.Format("/Query /TN \"{0}\" /XML", ScheduledTaskName), 10000).ConfigureAwait(false);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            return HasBatteryOrPowerRestrictionsInXml(output);
        }
        catch (Exception ex)
        {
            LogSuppressedException("HasBatteryOrPowerRestrictions", ex);
            return false;
        }
    }

    internal static bool HasBatteryOrPowerRestrictionsInXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return false;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
            var settings = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings");
            if (settings == null)
            {
                return HasBatteryOrPowerRestrictionsInTextFallback(xmlContent);
            }

            string GetElementValue(string localName) => settings.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

            string disallowBatteries = GetElementValue("DisallowStartIfOnBatteries");
            string stopOnBatteries = GetElementValue("StopIfGoingOnBatteries");
            string runOnlyIfIdle = GetElementValue("RunOnlyIfIdle");
            string executionTimeLimit = GetElementValue("ExecutionTimeLimit");

            var idleSettings = settings.Elements().FirstOrDefault(e => e.Name.LocalName == "IdleSettings");
            string stopOnIdleEnd = idleSettings?.Elements().FirstOrDefault(e => e.Name.LocalName == "StopOnIdleEnd")?.Value;

            bool hasDisallowBatteries = string.Equals(disallowBatteries, "true", StringComparison.OrdinalIgnoreCase);
            bool hasStopOnBattery = string.Equals(stopOnBatteries, "true", StringComparison.OrdinalIgnoreCase);
            bool hasIdleRestriction = string.Equals(runOnlyIfIdle, "true", StringComparison.OrdinalIgnoreCase);
            bool hasStopOnIdleEnd = string.Equals(stopOnIdleEnd, "true", StringComparison.OrdinalIgnoreCase);
            bool hasExecutionTimeout = !string.IsNullOrEmpty(executionTimeLimit) && !string.Equals(executionTimeLimit, "PT0S", StringComparison.OrdinalIgnoreCase);

            return hasDisallowBatteries || hasStopOnBattery || hasIdleRestriction || hasStopOnIdleEnd || hasExecutionTimeout;
        }
        catch
        {
            return HasBatteryOrPowerRestrictionsInTextFallback(xmlContent);
        }
    }

    private static bool HasBatteryOrPowerRestrictionsInTextFallback(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<RunOnlyIfIdle>true", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<StopOnIdleEnd>true", StringComparison.OrdinalIgnoreCase)
            || (!text.Contains("<ExecutionTimeLimit>PT0S", StringComparison.OrdinalIgnoreCase) && text.Contains("<ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<bool> ConfigureElevatedStartupTaskAsync(string executablePath = null, string userSid = null)
    {
        try
        {
            string targetExecutable = string.IsNullOrWhiteSpace(executablePath) ? GetStartupTaskExecutablePath() : executablePath;
            if (!IsElevated())
            {
                string arguments = ConfigureAdminStartupArgument;
                if (UpgradeUserContext.IsValidSid(userSid)) arguments += " " + StartupUserSidArgument + " " + userSid;
                bool elevated = await RunElevatedAdminCommandAsync(arguments).ConfigureAwait(false);
                return elevated && await HasElevatedStartupTaskAsync().ConfigureAwait(false);
            }

            return await ConfigureElevatedStartupTaskInCurrentProcessAsync(targetExecutable, userSid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSuppressedException("ConfigureElevatedStartupTask", ex);
            return false;
        }
    }

    private static async Task<bool> ConfigureElevatedStartupTaskInCurrentProcessAsync(string executablePath, string userSid = null)
    {
        try
        {
            if (!IsElevated())
            {
                return false;
            }

            string targetExecutable = string.IsNullOrWhiteSpace(executablePath) ? GetStartupTaskExecutablePath() : executablePath;
            if (!await CreateElevatedStartupTaskAsync(targetExecutable, elevated: true, userSid: userSid).ConfigureAwait(false))
            {
                return false;
            }

            PurgeAllRunKeys();
            PurgeRunKeysForUser(userSid);
            ExecutionTrace.LogEvent("StartupTaskHelper.ScheduledTask", "Configured", targetExecutable);
            return await HasElevatedStartupTaskAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSuppressedException("ConfigureElevatedStartupTaskInCurrentProcess", ex);
            return false;
        }
    }

    public static async Task<bool> HasElevatedStartupTaskAsync()
    {
        try
        {
            string[] taskNames = { ScheduledTaskName, "freesnip", "snapvox", "SnapVox" };
            foreach (var tn in taskNames)
            {
                int exitCode = await RunHiddenProcessAsync("schtasks.exe", string.Format("/Query /TN \"{0}\"", tn), 10000).ConfigureAwait(false);
                if (exitCode == 0) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            LogSuppressedException("HasElevatedStartupTask", ex);
            return false;
        }
    }


    public static async Task<bool> DeleteElevatedStartupTaskAsync()
    {
        try
        {
            if (!IsElevated())
            {
                bool elevated = await RunElevatedAdminCommandAsync(RemoveAdminStartupArgument).ConfigureAwait(false);
                return elevated && !await HasElevatedStartupTaskAsync().ConfigureAwait(false);
            }

            return await DeleteElevatedStartupTaskInCurrentProcessAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSuppressedException("DeleteElevatedStartupTask", ex);
            return false;
        }
    }

    private static async Task<bool> DeleteElevatedStartupTaskInCurrentProcessAsync()
    {
        try
        {
            if (!IsElevated())
            {
                return false;
            }

            int primaryExitCode = await RunHiddenProcessAsync("schtasks.exe", string.Format("/Delete /TN \"{0}\" /F", ScheduledTaskName), 10000).ConfigureAwait(false);
            string[] legacyTasks = { "freesnip", "snapvox", "SnapVox" };
            foreach (var tn in legacyTasks)
            {
                if (!string.Equals(tn, ScheduledTaskName, StringComparison.OrdinalIgnoreCase))
                {
                    await RunHiddenProcessAsync("schtasks.exe", string.Format("/Delete /TN \"{0}\" /F", tn), 10000).ConfigureAwait(false);
                }
            }
            StartupHelper.SetRunUser(null, GetStartupTaskExecutablePath());
            ExecutionTrace.LogEvent("StartupTaskHelper.ScheduledTask", "Delete", primaryExitCode.ToString());
            return primaryExitCode == 0 || !await HasElevatedStartupTaskAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSuppressedException("DeleteElevatedStartupTaskInCurrentProcess", ex);
            return false;
        }
    }

    private static async Task<bool> RunElevatedAdminCommandAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = RuntimePathHelper.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogSuppressedException("RunElevatedAdminCommand", ex);
            return false;
        }
    }

    public static async Task<bool> TryRunElevatedStartupTaskAsync()
    {
        try
        {
            Log?.Info("OS: Attempting to trigger elevated scheduled task...");
            int exitCode = await RunHiddenProcessAsync("schtasks.exe", string.Format("/Run /TN \"{0}\"", ScheduledTaskName), 10000).ConfigureAwait(false);
            Log?.Info("OS: Scheduled task execution trigger returned: " + exitCode);
            ExecutionTrace.LogEvent("StartupTaskHelper.ScheduledTask", "Run", exitCode.ToString());
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            LogSuppressedException("TryRunElevatedStartupTask", ex);
            return false;
        }
    }

    public static string[] GetSettingsCandidates()
    {
        var list = new List<string>
        {
            Path.Combine(ConfigurationFolder, "freesnip.ini"),
            Path.Combine(ConfigurationFolder, "snapvox.ini"),
            Path.Combine(InstallFolder, "freesnip.ini"),
            Path.Combine(InstallFolder, "snapvox.ini"),
            Path.Combine(InstallFolder, @"Data\Settings\freesnip.ini")
        };
        string defaultAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string defaultLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultProgFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (string.Equals(ConfigurationFolder, Path.Combine(defaultAppData, "FreeSnip"), StringComparison.OrdinalIgnoreCase))
        {
            list.Add(Path.Combine(defaultAppData, "SnapVox", "snapvox.ini"));
            list.Add(Path.Combine(defaultLocalAppData, "SnapVox", "snapvox.ini"));
            list.Add(Path.Combine(defaultLocalAppData, "FreeSnip", "freesnip.ini"));
            list.Add(Path.Combine(defaultProgFiles, "SnapVox", "snapvox.ini"));
            string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(progFilesX86))
            {
                list.Add(Path.Combine(progFilesX86, "SnapVox", "snapvox.ini"));
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<SettingsCandidate> GetProgramFilesSettingsCandidates(bool includeMachineLegacy)
    {
        yield return new SettingsCandidate(Path.Combine(InstallFolder, "freesnip.ini"), SettingsCandidateOrigin.ProgramFilesTemplate);
        yield return new SettingsCandidate(Path.Combine(InstallFolder, "snapvox.ini"), SettingsCandidateOrigin.ProgramFilesTemplate);
        yield return new SettingsCandidate(Path.Combine(InstallFolder, @"Data\Settings\freesnip.ini"), SettingsCandidateOrigin.ProgramFilesTemplate);
        if (!includeMachineLegacy) yield break;
        yield return new SettingsCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SnapVox", "snapvox.ini"), SettingsCandidateOrigin.ProgramFilesTemplate);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(progFilesX86))
            yield return new SettingsCandidate(Path.Combine(progFilesX86, "SnapVox", "snapvox.ini"), SettingsCandidateOrigin.ProgramFilesTemplate);
    }

    /// <summary>Classified candidates rooted at the static <see cref="ConfigurationFolder"/> (non-upgrade paths and legacy callers).</summary>
    internal static SettingsCandidate[] GetClassifiedSettingsCandidates()
    {
        string configurationFolder = ConfigurationFolder;
        var list = new List<SettingsCandidate>
        {
            new(Path.Combine(configurationFolder, "freesnip.ini"), SettingsCandidateOrigin.CanonicalUserProfile),
            new(Path.Combine(configurationFolder, "snapvox.ini"), SettingsCandidateOrigin.LegacyUserProfile)
        };
        string defaultAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string defaultLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool isDefaultFolder = string.Equals(configurationFolder, Path.Combine(defaultAppData, "FreeSnip"), StringComparison.OrdinalIgnoreCase);
        if (isDefaultFolder)
        {
            list.Add(new(Path.Combine(defaultAppData, "SnapVox", "snapvox.ini"), SettingsCandidateOrigin.LegacyUserProfile));
            list.Add(new(Path.Combine(defaultLocalAppData, "SnapVox", "snapvox.ini"), SettingsCandidateOrigin.LegacyUserProfile));
            list.Add(new(Path.Combine(defaultLocalAppData, "FreeSnip", "freesnip.ini"), SettingsCandidateOrigin.LegacyUserProfile));
        }
        list.AddRange(GetProgramFilesSettingsCandidates(isDefaultFolder));
        return list.ToArray();
    }

    /// <summary>Classified candidates rooted EXCLUSIVELY at the captured original user's profile (never the elevated worker's).</summary>
    internal static SettingsCandidate[] GetClassifiedSettingsCandidates(UpgradeUserContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        var list = new List<SettingsCandidate> { new(context.CanonicalSettingsPath, SettingsCandidateOrigin.CanonicalUserProfile) };
        list.AddRange(context.GetLegacyUserProfileCandidates().Select(p => new SettingsCandidate(p, SettingsCandidateOrigin.LegacyUserProfile)));
        list.AddRange(GetProgramFilesSettingsCandidates(includeMachineLegacy: true));
        return list.ToArray();
    }

    internal static void LogSettingsSelection(SettingsSelection selection, string scope)
    {
        foreach (var evaluation in selection.Evaluated.Where(e => e.Exists))
            LogInstallationElevationState($"[{scope}] Settings candidate: {evaluation}");
        LogInstallationElevationState(selection.Selected != null
            ? $"[{scope}] Selected settings source '{selection.Selected.Path}' ({selection.Selected.Origin}): {selection.Reason}"
            : $"[{scope}] No settings source selected: {selection.Reason}");
    }

    public static bool DetectAdminStartupInSettingsCandidates(IEnumerable<string> candidates = null)
    {
        try
        {
            foreach (string file in candidates ?? GetSettingsCandidates())
            {
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file);
                    if (text.IndexOf("RunAsAdministratorOnStartup=true", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("RunAsAdministratorOnStartup = true", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    public static void PurgeAllRunKeys()
    {
        string[] runSubKeys = {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run"
        };
        string[] valueNames = { "freesnip", "FreeSnip", "snapvox", "SnapVox" };

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var subKey in runSubKeys)
                    {
                        try
                        {
                            using var key = baseKey.OpenSubKey(subKey, true);
                            if (key == null) continue;
                            foreach (var name in valueNames)
                            {
                                try
                                {
                                    if (key.GetValue(name) != null)
                                    {
                                        key.DeleteValue(name, false);
                                        LogInstallationElevationState($"Purged Run key: {hive}\\{subKey}\\{name} ({view})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogSuppressedException("PurgeAllRunKeys.DeleteValue", ex);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogSuppressedException("PurgeAllRunKeys.OpenSubKey", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSuppressedException("PurgeAllRunKeys.OpenBaseKey", ex);
                }
            }
        }

        StartupHelper.DeleteStartupFolderShortcut();
    }

    private static readonly string[] UserRunSubKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run"
    };

    /// <summary>
    /// Removes FreeSnip Run values from the ORIGINAL user's loaded hive (HKEY_USERS\SID) when that user is not the
    /// current process identity. No-op for the current user (already handled by <see cref="PurgeAllRunKeys"/>).
    /// </summary>
    internal static void PurgeRunKeysForUser(string userSid)
    {
        if (IsCurrentUserSid(userSid) || !UpgradeUserContext.IsValidSid(userSid)) return;
        string[] valueNames = { "freesnip", "FreeSnip", "snapvox", "SnapVox" };
        foreach (string subKey in UserRunSubKeys)
        {
            try
            {
                using var key = Registry.Users.OpenSubKey(userSid + @"\" + subKey, true);
                if (key == null) continue;
                foreach (string name in valueNames)
                {
                    if (key.GetValue(name) == null) continue;
                    key.DeleteValue(name, false);
                    LogInstallationElevationState($"Purged Run key: HKU\\{userSid}\\{subKey}\\{name}");
                }
            }
            catch (Exception ex)
            {
                LogSuppressedException("PurgeRunKeysForUser", ex);
            }
        }
    }

    /// <summary>Registers normal (non-elevated) user startup for the original user SID.</summary>
    internal static void SetRunForUser(string userSid, string arguments, string executablePath)
    {
        if (IsCurrentUserSid(userSid))
        {
            StartupHelper.SetRunUser(arguments, executablePath);
            return;
        }

        if (!UpgradeUserContext.IsValidSid(userSid)) throw new ArgumentException("Invalid startup user SID.", nameof(userSid));
        string command = "\"" + executablePath + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim());
        using RegistryKey key = Registry.Users.CreateSubKey(userSid + @"\" + UserRunSubKeys[0], true)
            ?? throw new IOException("The original user's startup registry key could not be opened.");
        key.SetValue("FreeSnip", command);
        if (!string.Equals(key.GetValue("FreeSnip") as string, command, StringComparison.Ordinal))
            throw new IOException("Original user's startup registration could not be verified.");
        LogInstallationElevationState($"Standard Run startup registered for HKU\\{userSid}");
    }

    public static void LogInstallationElevationState(string message)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string path = DeploymentFootprint.TempInstallationLogPath;
                string logDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                string line = $"{DateTime.Now:HH:mm:ss.fff}|STARTUP_ELEVATION|INFO|{message}{Environment.NewLine}";
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(line);
                return;
            }
            catch (IOException)
            {
                if (attempt == 2) break;
                Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                LogSuppressedException("LogInstallationElevationState", ex);
                break;
            }
        }
    }

    public static async Task RestoreStartupAfterInstallAsync(bool keepUserSettings, bool hadElevatedStartup)
    {
        LogInstallationElevationState($"Beginning startup restoration: keepUserSettings={keepUserSettings}, hadElevatedStartup={hadElevatedStartup}");
        string primaryIni = Path.Combine(ConfigurationFolder, "freesnip.ini");
        var classified = GetClassifiedSettingsCandidates();
        var primarySelection = SettingsCandidateSelector.Select(classified);
        bool primaryIniExisted = primarySelection.Selected?.Origin == SettingsCandidateOrigin.CanonicalUserProfile;
        if (keepUserSettings && !primaryIniExisted)
        {
            LogSettingsSelection(primarySelection, "RestoreStartupAfterInstall");
            if (primarySelection.Selected != null)
            {
                try
                {
                    Directory.CreateDirectory(ConfigurationFolder);
                    string content = SettingsCandidateSelector.MigrateLegacyContent(File.ReadAllText(primarySelection.Selected.Path));
                    File.WriteAllText(primaryIni, content, Encoding.UTF8);
                    LogInstallationElevationState($"Migrated settings candidate '{primarySelection.Selected.Path}' to '{primaryIni}'");
                }
                catch (Exception ex)
                {
                    LogSuppressedException("RestoreStartupAfterInstallAsync.MigrateCandidate", ex);
                }
            }
        }

        IniConfig.IniDirectory = ConfigurationFolder;
        IniConfig.Init("FreeSnip", IniConfigurationDeployer.ConfigBaseName);
        var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
        bool candidatesHadElevated = !primaryIniExisted && DetectAdminStartupInSettingsCandidates();
        bool elevated = keepUserSettings && (hadElevatedStartup || config.RunAsAdministratorOnStartup || candidatesHadElevated);
        LogInstallationElevationState($"Evaluated elevation requirement: hadElevatedStartup={hadElevatedStartup}, configFlag={config.RunAsAdministratorOnStartup}, candidatesHadElevated={candidatesHadElevated} -> effectiveElevated={elevated}");

        if (elevated)
        {
            LogInstallationElevationState("Configuring elevated scheduled task...");
            if (!await ConfigureElevatedStartupTaskAsync(InstallPath).ConfigureAwait(false))
            {
                LogInstallationElevationState("FAILED to configure elevated scheduled task.");
                throw new IOException("Could not restore administrator startup. Your settings backup has been kept.");
            }
            LogInstallationElevationState("Elevated scheduled task configured successfully. Purging Run registry entries to prevent dual startup.");
            PurgeAllRunKeys();
        }
        else
        {
            LogInstallationElevationState("Configuring standard non-elevated user startup in HKCU Run...");
            await DeleteElevatedStartupTaskAsync().ConfigureAwait(false);
            PurgeAllRunKeys();
            StartupHelper.SetRunUser("--autorun", InstallPath);
            LogInstallationElevationState("Standard user Run startup configured.");
        }

        config.RunAsAdministratorOnStartup = elevated;
        string primaryDir = Path.GetDirectoryName(primaryIni);
        if (!string.IsNullOrEmpty(primaryDir) && !Directory.Exists(primaryDir))
        {
            Directory.CreateDirectory(primaryDir);
        }
        IniConfig.SaveTo(primaryIni);
        IniConfig.Save();
        foreach (string candidate in GetSettingsCandidates())
        {
            if (File.Exists(candidate) && !string.Equals(candidate, primaryIni, StringComparison.OrdinalIgnoreCase))
            {
                try { IniConfig.SaveTo(candidate); } catch { }
            }
        }
        LogInstallationElevationState($"Startup restoration finalized with RunAsAdministratorOnStartup={elevated}");
    }

    internal readonly record struct UpgradeStartupResult(bool RunAsAdministratorOnStartup, bool SettingsFileModified);

    /// <summary>
    /// KEEP-SETTINGS / auto-update startup restoration for the ORIGINAL user captured before UAC.
    /// Never derives ownership from the worker identity: the elevated task is created for <see cref="UpgradeUserContext.UserSid"/>,
    /// normal startup is written to that user's hive, and the restored INI is only edited (single key, byte-preserving) when
    /// RunAsAdministratorOnStartup must be reconciled with the configured startup mode.
    /// </summary>
    internal static async Task<UpgradeStartupResult> RestoreStartupForUpgradeAsync(UpgradeUserContext context, bool hadElevatedStartup)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        string ini = context.CanonicalSettingsPath;
        string text = File.Exists(ini) ? await File.ReadAllTextAsync(ini, Encoding.UTF8).ConfigureAwait(false) : null;
        bool iniFlag = text != null && SettingsCandidateSelector.ReadRunAsAdministrator(text) == true;
        bool elevated = hadElevatedStartup || iniFlag;
        LogInstallationElevationState($"Upgrade startup restoration for SID {context.UserSid}: hadElevatedStartup={hadElevatedStartup}, iniFlag={iniFlag} -> effectiveElevated={elevated}, ini='{ini}'");

        if (elevated)
        {
            if (!await ConfigureElevatedStartupTaskAsync(InstallPath, context.UserSid).ConfigureAwait(false))
            {
                LogInstallationElevationState("FAILED to configure elevated scheduled task for original user.");
                throw new IOException("Could not restore administrator startup. Your settings backup has been kept.");
            }
            PurgeAllRunKeys();
            PurgeRunKeysForUser(context.UserSid);
            LogInstallationElevationState("Elevated scheduled task configured successfully. Purging Run registry entries to prevent dual startup.");
        }
        else
        {
            await DeleteElevatedStartupTaskAsync().ConfigureAwait(false);
            PurgeAllRunKeys();
            PurgeRunKeysForUser(context.UserSid);
            SetRunForUser(context.UserSid, "--autorun", InstallPath);
            LogInstallationElevationState("Standard user Run startup configured for original user.");
        }

        bool modified = false;
        Directory.CreateDirectory(context.ConfigurationFolder);
        if (text != null && iniFlag != elevated)
        {
            string updated = SettingsCandidateSelector.SetIniValue(text, "Core", "RunAsAdministratorOnStartup", elevated ? "True" : "False");
            string temporary = ini + ".startup-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] original = await File.ReadAllBytesAsync(ini).ConfigureAwait(false);
                bool hadBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
                await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(hadBom)).ConfigureAwait(false);
                File.Move(temporary, ini, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            modified = true;
        }

        IniConfig.IniDirectory = context.ConfigurationFolder;
        IniConfig.Init("FreeSnip", IniConfigurationDeployer.ConfigBaseName);
        var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
        if (text == null)
        {
            config.RunAsAdministratorOnStartup = elevated;
            IniConfig.SaveTo(ini);
            modified = true;
        }
        else if (config.RunAsAdministratorOnStartup != elevated)
        {
            throw new IOException("Reloaded settings do not reflect the restored administrator-startup state.");
        }

        LogInstallationElevationState($"Upgrade startup restoration finalized with RunAsAdministratorOnStartup={elevated}, settingsModified={modified}");
        return new UpgradeStartupResult(elevated, modified);
    }

    public static bool IsRunningFromInstallPath()
    {
        try
        {
            string currentExecutable = Path.GetFullPath(RuntimePathHelper.ExecutablePath).TrimEnd(Path.DirectorySeparatorChar);
            string expectedInstallPath = Path.GetFullPath(InstallPath).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(currentExecutable, expectedInstallPath, StringComparison.OrdinalIgnoreCase)) return true;
            string expectedUninstallPath = Path.GetFullPath(UninstallExePath).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(currentExecutable, expectedUninstallPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            string executablePath = RuntimePathHelper.ExecutablePath;
            ExecutionTrace.LogEvent("StartupTaskHelper", "InstallPathFallback", executablePath);
            return string.Equals(executablePath, InstallPath, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(executablePath, UninstallExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static bool IsInstalledExecutable(string executablePath, string installFolder = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        try
        {
            string root = Path.GetFullPath(installFolder ?? InstallFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(executablePath);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }
    }

    internal static int[] FindRunningInstalledProcesses()
    {
        var ids = new System.Collections.Generic.List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (IsInstalledExecutable(process.MainModule?.FileName)) ids.Add(process.Id);
                }
                catch (Exception ex) when (IsExpectedProcessInspectionException(ex)) { }
            }
        }
        return ids.ToArray();
    }

    internal static void RequireInstalledApplicationsClosed()
    {
        if (FindRunningInstalledProcesses().Length != 0)
            throw new IOException("FreeSnip is still running. Save your work, exit FreeSnip from its tray menu, and run setup again. No application was forcibly closed.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);


    private const uint MbOk = 0x00000000;
    private const uint MbOkCancel = 0x00000001;
    private const uint MbAbortRetryIgnore = 0x00000002;
    private const uint MbYesNoCancel = 0x00000003;
    private const uint MbYesNo = 0x00000004;
    private const uint MbRetryCancel = 0x00000005;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopmost = 0x00040000;


    private const int IdCancel = 2;
    private const int IdAbort = 3;
    private const int IdRetry = 4;
    private const int IdIgnore = 5;
    private const int IdYes = 6;
    private const int IdNo = 7;

    internal static Func<string, string, MessageBoxButtons, MessageBoxIcon, IntPtr, DialogResult?> MessageBoxHook { get; set; }

    public static DialogResult ShowForegroundMessageBox(string message, string title, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information, IntPtr ownerHWnd = default)
    {
        if (MessageBoxHook != null)
        {
            var hooked = MessageBoxHook(message, title, buttons, icon, ownerHWnd);
            if (hooked.HasValue) return hooked.Value;
        }

        try
        {
            uint type = MbOk;
            if (buttons == MessageBoxButtons.OKCancel) type = MbOkCancel;
            else if (buttons == MessageBoxButtons.AbortRetryIgnore) type = MbAbortRetryIgnore;
            else if (buttons == MessageBoxButtons.YesNoCancel) type = MbYesNoCancel;
            else if (buttons == MessageBoxButtons.YesNo) type = MbYesNo;
            else if (buttons == MessageBoxButtons.RetryCancel) type = MbRetryCancel;

            if (icon == MessageBoxIcon.Hand || icon == MessageBoxIcon.Stop || icon == MessageBoxIcon.Error) type |= MbIconError;
            else if (icon == MessageBoxIcon.Question) type |= MbIconQuestion;
            else if (icon == MessageBoxIcon.Exclamation || icon == MessageBoxIcon.Warning) type |= MbIconWarning;
            else if (icon == MessageBoxIcon.None) {  }
            else type |= MbIconInformation;

            type |= MbSetForeground | MbTopmost;

            int result = MessageBox(ownerHWnd, message, title, type);

            if (result == IdYes) return DialogResult.Yes;
            if (result == IdNo) return DialogResult.No;
            if (result == IdCancel) return DialogResult.Cancel;
            if (result == IdAbort) return DialogResult.Abort;
            if (result == IdRetry) return DialogResult.Retry;
            if (result == IdIgnore) return DialogResult.Ignore;
            return DialogResult.OK;
        }
        catch
        {


            return buttons == MessageBoxButtons.OK ? DialogResult.OK : DialogResult.Cancel;
        }
    }
}


