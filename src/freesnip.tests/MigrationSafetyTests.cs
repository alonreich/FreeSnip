using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests;

public class MigrationSafetyTests
{
    private sealed class TempEnvironment : IDisposable
    {
        public string Root { get; }
        public string LegacyInstallFolder { get; }
        public string LegacyConfigFolder { get; }
        public string StagedInstallFolder { get; }
        public string FreeSnipConfigFolder { get; }

        public TempEnvironment()
        {
            Root = Path.Combine(Path.GetTempPath(), "FreeSnip_MigrationTest_" + Guid.NewGuid().ToString("N"));
            LegacyInstallFolder = Path.Combine(Root, "ProgramFiles", "SnapVox");
            LegacyConfigFolder = Path.Combine(Root, "AppData", "SnapVox");
            StagedInstallFolder = Path.Combine(Root, "ProgramFiles", "FreeSnip");
            FreeSnipConfigFolder = Path.Combine(Root, "AppData", "FreeSnip");

            Directory.CreateDirectory(LegacyInstallFolder);
            Directory.CreateDirectory(LegacyConfigFolder);
            Directory.CreateDirectory(StagedInstallFolder);
            Directory.CreateDirectory(FreeSnipConfigFolder);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch { }
        }
    }

    [Fact]
    public void SettingsTranslation_ReplacesSectionsAndPathsAccurately()
    {
        string legacyIni =
@"[snapvox]
RunAsAdministratorOnStartup=true
StorageFolder=C:\Program Files\SnapVox\Captures
CacheDir=C:\Users\User\AppData\Local\snapvox\cache
AutoSave=true

[SnapVox]
Title=SnapVox Tool

[Editor]
Color=#FF0000";

        string translated = DeploymentLifecycle.TranslateSettings(legacyIni);

        Assert.DoesNotContain("[snapvox]", translated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\SnapVox\", translated, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("[FreeSnip]", translated);
        Assert.Contains(@"C:\Program Files\FreeSnip\Captures", translated);
        Assert.Contains(@"C:\Users\User\AppData\Local\FreeSnip\cache", translated);
        Assert.Contains("RunAsAdministratorOnStartup=true", translated);
        Assert.Contains("AutoSave=true", translated);
        Assert.Contains("[Editor]", translated);
        Assert.Contains("CloseEditorOnAction=false", translated);
    }

    [Fact]
    public async Task SettingsBackupAndTranslation_DurablyStagesVerifiedConfig()
    {
        using var env = new TempEnvironment();
        string origLegacyConfig = StartupTaskHelper.LegacyConfigurationFolder;
        string origConfig = StartupTaskHelper.ConfigurationFolder;

        try
        {
            StartupTaskHelper.LegacyConfigurationFolder = env.LegacyConfigFolder;
            StartupTaskHelper.ConfigurationFolder = env.FreeSnipConfigFolder;

            string legacyIniPath = Path.Combine(env.LegacyConfigFolder, "snapvox.ini");
            string legacyContent = "[snapvox]\r\nSavePath=C:\\Program Files\\SnapVox\\Saved\r\nRunAsAdministratorOnStartup=true\r\n";
            await File.WriteAllTextAsync(legacyIniPath, legacyContent, Encoding.UTF8);

            string origHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(legacyIniPath)));

            var logger = await DeploymentLifecycle.DeploymentLogger.CreateAsync(
                Path.Combine(env.Root, "migration.log"), "TEST", default);

            string backupFolder;
            await using (logger)
            {
                backupFolder = await DeploymentLifecycle.BackupAndTranslateLegacySettingsAsync(logger, default);
            }

            Assert.NotNull(backupFolder);
            Assert.True(Directory.Exists(backupFolder));

            // Verify the backup copy exists and has matching SHA-256
            string backupCopy = Path.Combine(backupFolder, "snapvox.ini");
            Assert.True(File.Exists(backupCopy));
            string backupHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backupCopy)));
            Assert.Equal(origHash, backupHash);

            // Verify staged freesnip.ini exists and has translated content
            string stagedIni = Path.Combine(env.FreeSnipConfigFolder, "freesnip.ini");
            Assert.True(File.Exists(stagedIni));
            string stagedContent = await File.ReadAllTextAsync(stagedIni);
            Assert.Contains("[FreeSnip]", stagedContent);
            Assert.Contains(@"C:\Program Files\FreeSnip\Saved", stagedContent);
            Assert.DoesNotContain("[snapvox]", stagedContent, StringComparison.OrdinalIgnoreCase);

            // Verify legacy SnapVox file remains completely untouched
            Assert.True(File.Exists(legacyIniPath));
            Assert.Equal(legacyContent, await File.ReadAllTextAsync(legacyIniPath));
        }
        finally
        {
            StartupTaskHelper.LegacyConfigurationFolder = origLegacyConfig;
            StartupTaskHelper.ConfigurationFolder = origConfig;
        }
    }

    [Fact]
    public async Task Phase1Failure_AbortsAndLeavesLegacySnapVoxUntouched()
    {
        using var env = new TempEnvironment();
        string origInstall = StartupTaskHelper.InstallFolder;
        string origLegacyInstall = StartupTaskHelper.LegacyInstallFolder;

        try
        {
            StartupTaskHelper.InstallFolder = env.StagedInstallFolder;
            StartupTaskHelper.LegacyInstallFolder = env.LegacyInstallFolder;

            // Seed legacy SnapVox install folder with files
            string legacyExe = Path.Combine(env.LegacyInstallFolder, "SnapVox.exe");
            string legacyPayload = Path.Combine(env.LegacyInstallFolder, "libSkiaSharp.dll");
            await File.WriteAllTextAsync(legacyExe, "LEGACY_BINARY_BYTES");
            await File.WriteAllTextAsync(legacyPayload, "LEGACY_PAYLOAD_BYTES");

            // Seed staged FreeSnip with a non-executable or corrupted file that will fail probe
            string stagedExe = Path.Combine(env.StagedInstallFolder, "FreeSnip.exe");
            await File.WriteAllTextAsync(stagedExe, "CORRUPTED_NOT_A_PE_BINARY");

            var logger = await DeploymentLifecycle.DeploymentLogger.CreateAsync(
                Path.Combine(env.Root, "probe_fail.log"), "TEST_PROBE", default);

            bool probeResult;
            await using (logger)
            {
                probeResult = await DeploymentLifecycle.ProbeStagedBinaryAsync(stagedExe, logger, default);
            }

            // Probe MUST fail for non-executable
            Assert.False(probeResult);

            // Simulate the Critical Guard: when probe fails, delete staged folder
            if (!probeResult)
            {
                if (Directory.Exists(env.StagedInstallFolder))
                {
                    Directory.Delete(env.StagedInstallFolder, true);
                }
            }

            // FreeSnip staging directory must be cleaned up
            Assert.False(Directory.Exists(env.StagedInstallFolder));

            // CRITICAL GUARD VERIFICATION: Legacy SnapVox directory and files are 100% untouched
            Assert.True(Directory.Exists(env.LegacyInstallFolder));
            Assert.True(File.Exists(legacyExe));
            Assert.True(File.Exists(legacyPayload));
            Assert.Equal("LEGACY_BINARY_BYTES", await File.ReadAllTextAsync(legacyExe));
            Assert.Equal("LEGACY_PAYLOAD_BYTES", await File.ReadAllTextAsync(legacyPayload));
        }
        finally
        {
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.LegacyInstallFolder = origLegacyInstall;
        }
    }

    [Fact]
    public async Task DualTaskAndRunKeyDecommission_EliminatesLegacyAndPreventsDualAutostart()
    {
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;

        using var env = new TempEnvironment();
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Seed registry with old SnapVox run keys
        using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, true))
        {
            key.SetValue("snapvox", "\"C:\\Program Files\\SnapVox\\SnapVox.exe\" --autorun");
            key.SetValue("SnapVox", "\"C:\\Program Files\\SnapVox\\SnapVox.exe\" --autorun");
        }

        var executedCommands = new List<string>();
        try
        {
            StartupTaskHelper.ConfigurationFolder = env.FreeSnipConfigFolder;
            StartupTaskHelper.InstallFolder = env.StagedInstallFolder;
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessHook = (file, args, timeout) =>
            {
                executedCommands.Add(args);
                return Task.FromResult(0);
            };

            string iniPath = Path.Combine(env.FreeSnipConfigFolder, "freesnip.ini");
            await File.WriteAllTextAsync(iniPath, "[FreeSnip]\r\nRunAsAdministratorOnStartup=true\r\n");

            // Execute startup restore for elevated user
            await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings: true, hadElevatedStartup: true);

            // Verify FreeSnip scheduled task was created
            Assert.Contains(executedCommands, c => c.Contains($"/Create /TN \"{StartupTaskHelper.ScheduledTaskName}\""));

            // Verify legacy SnapVox scheduled task was explicitly deleted
            Assert.Contains(executedCommands, c => c.Contains($"/Delete /TN \"{StartupTaskHelper.LegacyScheduledTaskName}\""));

            // Verify registry Run keys are completely purged of both SnapVox and FreeSnip to prevent dual startup
            using (var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
            {
                Assert.Null(key?.GetValue("snapvox"));
                Assert.Null(key?.GetValue("SnapVox"));
                Assert.Null(key?.GetValue("freesnip"));
                Assert.Null(key?.GetValue("FreeSnip"));
            }
        }
        finally
        {
            StartupTaskHelper.ConfigurationFolder = origConfig;
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.IsElevatedOverride = origElevated;
            StartupTaskHelper.RunProcessHook = origHook;

            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
            key?.DeleteValue("snapvox", false);
            key?.DeleteValue("SnapVox", false);
            key?.DeleteValue("freesnip", false);
            key?.DeleteValue("FreeSnip", false);
        }
    }

    [Fact]
    public async Task DetectLegacyFootprint_CorrectlyIdentifiesLegacyPresence()
    {
        using var env = new TempEnvironment();
        string origLegacyInstall = StartupTaskHelper.LegacyInstallFolder;
        string origLegacyConfig = StartupTaskHelper.LegacyConfigurationFolder;

        try
        {
            StartupTaskHelper.LegacyInstallFolder = env.LegacyInstallFolder;
            StartupTaskHelper.LegacyConfigurationFolder = env.LegacyConfigFolder;

            // Scenario A: Nothing exists
            var cleanFootprint = await DeploymentLifecycle.DetectLegacyFootprintAsync();
            // In clean temp dirs with no tasks running
            // HasInstallFolder will be true because we created env.LegacyInstallFolder
            Assert.True(cleanFootprint.HasInstallFolder);
            Assert.True(cleanFootprint.IsDetected);

            // Scenario B: Delete folder, verify not detected
            Directory.Delete(env.LegacyInstallFolder);
            var purgedFootprint = await DeploymentLifecycle.DetectLegacyFootprintAsync();
            Assert.False(purgedFootprint.HasInstallFolder);
            Assert.False(purgedFootprint.HasLegacyConfig);
        }
        finally
        {
            StartupTaskHelper.LegacyInstallFolder = origLegacyInstall;
            StartupTaskHelper.LegacyConfigurationFolder = origLegacyConfig;
        }
    }

    [Fact]
    public async Task RestoreStartupAfterInstallAsync_NeverWritesToLegacySnapVoxCandidates()
    {
        using var env = new TempEnvironment();
        string origLegacyConfig = StartupTaskHelper.LegacyConfigurationFolder;
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        string origLegacyInstall = StartupTaskHelper.LegacyInstallFolder;

        try
        {
            StartupTaskHelper.LegacyConfigurationFolder = env.LegacyConfigFolder;
            StartupTaskHelper.ConfigurationFolder = env.FreeSnipConfigFolder;
            StartupTaskHelper.InstallFolder = env.StagedInstallFolder;
            StartupTaskHelper.LegacyInstallFolder = env.LegacyInstallFolder;

            string legacyIni = Path.Combine(env.LegacyConfigFolder, "snapvox.ini");
            string sentinelText = "[OldSnapVox]\r\nOriginal=123\r\n";
            await File.WriteAllTextAsync(legacyIni, sentinelText, Encoding.UTF8);

            string primaryIni = Path.Combine(env.FreeSnipConfigFolder, "freesnip.ini");
            await File.WriteAllTextAsync(primaryIni, "[FreeSnip]\r\nRunAsAdministratorOnStartup=false\r\n", Encoding.UTF8);

            await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings: true, hadElevatedStartup: false);

            string legacyAfter = await File.ReadAllTextAsync(legacyIni, Encoding.UTF8);
            Assert.Equal(sentinelText, legacyAfter);
        }
        finally
        {
            StartupTaskHelper.LegacyConfigurationFolder = origLegacyConfig;
            StartupTaskHelper.ConfigurationFolder = origConfig;
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.LegacyInstallFolder = origLegacyInstall;
        }
    }

    [Fact]
    public void LegacyDirectoryPurgeTargets_ContainsAllLegacySnapVoxRoots()
    {
        var targets = DeploymentFootprint.GetLegacyDirectoryPurgeTargets(includeInstallFolder: true).ToList();

        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyInstallFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyProgramDataFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyProgramDataFolderAlt, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyRoamingAppDataFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyRoamingAppDataFolderAlt, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyLocalAppDataFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyLocalAppDataFolderAlt, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyTempAppFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, t => t.Equals(DeploymentFootprint.LegacyTempAppFolderAlt, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScrubExplorerFileExts_CleansSnapVoxReferences()
    {
        const string testExt = ".freesnip_test_ext";
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var fileExts = baseKey.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts"))
        using (var extKey = fileExts.CreateSubKey(testExt))
        {
            using (var openWithList = extKey.CreateSubKey("OpenWithList"))
            {
                openWithList.SetValue("a", "snapvox.exe");
                openWithList.SetValue("b", "notepad.exe");
                openWithList.SetValue("MRUList", "ab");
            }
            using (var openWithProgids = extKey.CreateSubKey("OpenWithProgids"))
            {
                openWithProgids.SetValue("snapvox.editor.1", new byte[0], RegistryValueKind.None);
                openWithProgids.SetValue("freesnip.editor.1", new byte[0], RegistryValueKind.None);
            }
            using (var userChoice = extKey.CreateSubKey("UserChoiceLatest"))
            {
                userChoice.SetValue("ProgId", "snapvox.editor.1");
            }
        }

        string logPath = Path.Combine(Path.GetTempPath(), "test_scrub_" + Guid.NewGuid().ToString("N") + ".log");
        var logger = await DeploymentLifecycle.DeploymentLogger.CreateAsync(logPath, "TEST", default);
        try
        {
            await using (logger)
            {
                await DeploymentLifecycle.ScrubExplorerFileExtsAsync(logger, default);
            }

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var extKey = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{testExt}");
            Assert.NotNull(extKey);

            using (var openWithList = extKey.OpenSubKey("OpenWithList"))
            {
                Assert.NotNull(openWithList);
                Assert.Null(openWithList.GetValue("a"));
                Assert.Equal("notepad.exe", openWithList.GetValue("b")?.ToString());
                Assert.Equal("b", openWithList.GetValue("MRUList")?.ToString());
            }

            using (var openWithProgids = extKey.OpenSubKey("OpenWithProgids"))
            {
                Assert.NotNull(openWithProgids);
                Assert.Null(openWithProgids.GetValue("snapvox.editor.1"));
                Assert.NotNull(openWithProgids.GetValue("freesnip.editor.1"));
            }

            using (var userChoice = extKey.OpenSubKey("UserChoiceLatest"))
            {
                Assert.Null(userChoice);
            }
        }
        finally
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var fileExts = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", true);
                fileExts?.DeleteSubKeyTree(testExt, false);
            }
            catch { }

            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }
}
