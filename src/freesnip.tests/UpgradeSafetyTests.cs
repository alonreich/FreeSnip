using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.helpers;

namespace freesnip.tests;

public class UpgradeSafetyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\SnapVox\SnapVox.exe", true)]
    [InlineData(@"C:\Program Files\SnapVox\Uninstall.exe", true)]
    [InlineData(@"C:\Program Files\OtherApp\Uninstall.exe", false)]
    [InlineData(@"C:\Program Files\SnapVoxOther\Uninstall.exe", false)]
    [InlineData(@"C:\Program Files\SnapVox\..\OtherApp\Uninstall.exe", false)]
    [InlineData(@"C:\Users\User\Downloads\SnapVox.exe", false)]
    public void ProcessOwnership_RequiresActualInstallDirectory(string executable, bool expected)
        => Assert.Equal(expected, StartupTaskHelper.IsInstalledExecutable(executable, @"C:\Program Files\SnapVox"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_HasNoBatteryIdleNetworkOrTimeLimitRestrictions(bool elevated)
    {
        string executable = @"C:\Apps & Tools\SnapVox.exe";
        var xml = XDocument.Parse(StartupTaskDefinition.Create(executable, "S-1-5-21-123", elevated));
        XNamespace ns = StartupTaskDefinition.Namespace;
        var settings = xml.Root!.Element(ns + "Settings")!;
        foreach (string flag in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "RunOnlyIfIdle", "RunOnlyIfNetworkAvailable", "AllowHardTerminate" })
            Assert.Equal("false", settings.Element(ns + flag)!.Value);
        Assert.Equal("true", settings.Element(ns + "StartWhenAvailable")!.Value);
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")!.Value);
        var idle = settings.Element(ns + "IdleSettings")!;
        Assert.Equal("false", idle.Element(ns + "StopOnIdleEnd")!.Value);
        Assert.Equal("false", idle.Element(ns + "RestartOnIdle")!.Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(elevated ? "HighestAvailable" : "LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal(executable, xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--autorun", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("S-1-5-21-123", xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
    }

    [Fact]
    public async Task SettingsBackup_RestoresExactSettingsIncludingElevation()
    {
        using var files = new TestFiles();
        string original = "[Core]\r\nRunAsAdministratorOnStartup=True\r\nRegionHotkey=Alt + A\r\n";
        await File.WriteAllTextAsync(files.Settings, original);
        string backup = await UpgradeSettingsBackup.CreateAsync(new[] { files.Settings, files.Settings.ToUpperInvariant() }, files.BackupRoot);
        Assert.Single(Directory.GetFiles(backup, "*.ini"));
        File.Delete(files.Settings);
        await UpgradeSettingsBackup.RestoreAsync(backup, new[] { files.Settings });
        Assert.Equal(original, await File.ReadAllTextAsync(files.Settings));
        Assert.True(File.Exists(Path.Combine(backup, "manifest.txt")));
    }

    [Fact]
    public async Task FailedBackup_StopsWithoutChangingOriginalSettings()
    {
        using var files = new TestFiles();
        await File.WriteAllTextAsync(files.Settings, "preserve me");
        using var locked = new FileStream(files.Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => UpgradeSettingsBackup.CreateAsync(new[] { files.Settings }, files.BackupRoot));
        Assert.Equal(11, locked.Length);
    }

    [Fact]
    public async Task DamagedBackup_IsRejectedBeforeReplacingAnySettings()
    {
        using var files = new TestFiles();
        await File.WriteAllTextAsync(files.Settings, "original");
        string backup = await UpgradeSettingsBackup.CreateAsync(new[] { files.Settings }, files.BackupRoot);
        await File.WriteAllTextAsync(files.Settings, "new installation");
        await File.WriteAllTextAsync(Path.Combine(backup, "settings_0.ini"), "damaged");
        await Assert.ThrowsAsync<IOException>(() => UpgradeSettingsBackup.RestoreAsync(backup, new[] { files.Settings }));
        Assert.Equal("new installation", await File.ReadAllTextAsync(files.Settings));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public async Task FailedRestore_KeepsRecoveryCopyForRetry()
    {
        using var files = new TestFiles();
        await File.WriteAllTextAsync(files.Settings, "original settings");
        string backup = await UpgradeSettingsBackup.CreateAsync(new[] { files.Settings }, files.BackupRoot);
        using (var locked = new FileStream(files.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => UpgradeSettingsBackup.RestoreAsync(backup, new[] { files.Settings }));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("original settings", await File.ReadAllTextAsync(Path.Combine(backup, "settings_0.ini")));
        await UpgradeSettingsBackup.RestoreAsync(backup, new[] { files.Settings });
        Assert.Equal("original settings", await File.ReadAllTextAsync(files.Settings));
    }

    [Fact]
    public void DetectAdminStartupInSettingsCandidates_IdentifiesFlagsCorrectly()
    {
        using var files = new TestFiles();
        string file1 = Path.Combine(files.ConfigFolder, "cfg1.ini");
        string file2 = Path.Combine(files.ConfigFolder, "cfg2.ini");
        string file3 = Path.Combine(files.ConfigFolder, "cfg3.ini");

        File.WriteAllText(file1, "[Core]\r\nRunAsAdministratorOnStartup=true\r\n");
        Assert.True(StartupTaskHelper.DetectAdminStartupInSettingsCandidates(new[] { file1 }));

        File.WriteAllText(file2, "[Core]\r\nRunAsAdministratorOnStartup = true\r\n");
        Assert.True(StartupTaskHelper.DetectAdminStartupInSettingsCandidates(new[] { file2 }));

        File.WriteAllText(file3, "[Core]\r\nRunAsAdministratorOnStartup=false\r\n");
        Assert.False(StartupTaskHelper.DetectAdminStartupInSettingsCandidates(new[] { file3 }));

        Assert.False(StartupTaskHelper.DetectAdminStartupInSettingsCandidates(new[] { Path.Combine(files.ConfigFolder, "nonexistent.ini") }));
    }

    [Fact]
    public void PurgeAllRunKeys_RemovesAllRunValues()
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, true))
        {
            key.SetValue("snapvox", "\"C:\\dummy\\snapvox.exe\" --autorun");
            key.SetValue("SnapVox", "\"C:\\dummy\\SnapVox.exe\" --autorun");
        }

        try
        {
            StartupTaskHelper.PurgeAllRunKeys();
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false);
            Assert.Null(key?.GetValue("snapvox"));
            Assert.Null(key?.GetValue("SnapVox"));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
            key?.DeleteValue("snapvox", false);
            key?.DeleteValue("SnapVox", false);
        }
    }

    [Fact]
    public async Task RestoreStartupAfterInstallAsync_WhenElevated_RegistersScheduledTask_AndPurgesRunKeys()
    {
        using var files = new TestFiles();
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath, true))
        {
            key.SetValue("snapvox", "\"C:\\dummy\\snapvox.exe\" --autorun");
            key.SetValue("SnapVox", "\"C:\\dummy\\SnapVox.exe\" --autorun");
        }

        var commands = new List<string>();
        try
        {
            StartupTaskHelper.ConfigurationFolder = files.ConfigFolder;
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessHook = (file, args, timeout) =>
            {
                commands.Add(args);
                return Task.FromResult(0);
            };

            string iniPath = Path.Combine(files.ConfigFolder, "freesnip.ini");
            await File.WriteAllTextAsync(iniPath, "[Core]\r\nRunAsAdministratorOnStartup=true\r\n");

            await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings: true, hadElevatedStartup: true);

            Assert.Contains(commands, c => c.Contains("/Create /TN \"FreeSnip\""));
            using (var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
            {
                Assert.Null(key?.GetValue("snapvox"));
                Assert.Null(key?.GetValue("SnapVox"));
                Assert.Null(key?.GetValue("freesnip"));
                Assert.Null(key?.GetValue("FreeSnip"));
            }

            string savedIni = await File.ReadAllTextAsync(iniPath);
            Assert.Contains("RunAsAdministratorOnStartup=True", savedIni, StringComparison.OrdinalIgnoreCase);

            string logPath = DeploymentFootprint.TempInstallationLogPath;
            Assert.True(File.Exists(logPath));
            string logText = await File.ReadAllTextAsync(logPath);
            Assert.Contains("Elevated scheduled task configured successfully", logText);
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
    public async Task RestoreStartupAfterInstallAsync_WhenNotElevated_ConfiguresStandardRunKey_AndPurgesScheduledTask()
    {
        using var files = new TestFiles();
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        string origLegacyConfig = StartupTaskHelper.LegacyConfigurationFolder;
        string origLegacyInstall = StartupTaskHelper.LegacyInstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var commands = new List<string>();
        try
        {
            StartupTaskHelper.ConfigurationFolder = files.ConfigFolder;
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            StartupTaskHelper.LegacyConfigurationFolder = files.ConfigFolder;
            StartupTaskHelper.LegacyInstallFolder = files.InstallFolder;
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessHook = (file, args, timeout) =>
            {
                commands.Add(args);
                if (args.Contains("/Query /TN \"snapvox\"")) return Task.FromResult(1);
                return Task.FromResult(0);
            };

            string iniPath = Path.Combine(files.ConfigFolder, "freesnip.ini");
            await File.WriteAllTextAsync(iniPath, "[Core]\r\nRunAsAdministratorOnStartup=false\r\n");

            await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings: true, hadElevatedStartup: false);

            Assert.Contains(commands, c => c.Contains("/Delete /TN \"FreeSnip\""));
            using (var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
            {
                object? val = key?.GetValue("FreeSnip");
                Assert.NotNull(val);
                string valStr = val.ToString()!;
                Assert.Contains("--autorun", valStr);
                Assert.Contains(files.InstallFolder, valStr);
            }

            string savedIni = await File.ReadAllTextAsync(iniPath);
            Assert.Contains("RunAsAdministratorOnStartup=False", savedIni, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StartupTaskHelper.ConfigurationFolder = origConfig;
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.LegacyConfigurationFolder = origLegacyConfig;
            StartupTaskHelper.LegacyInstallFolder = origLegacyInstall;
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
    public void HasBatteryOrPowerRestrictionsInXml_DetectsRestrictionsAccurately()
    {
        string defaultWindowsTaskXml = @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Settings>
    <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
    <RunOnlyIfIdle>true</RunOnlyIfIdle>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
    </IdleSettings>
  </Settings>
</Task>";
        Assert.True(StartupTaskHelper.HasBatteryOrPowerRestrictionsInXml(defaultWindowsTaskXml));

        string cleanXml = StartupTaskDefinition.Create(@"C:\SnapVox\snapvox.exe", "S-1-5-21-12345", elevated: true);
        Assert.False(StartupTaskHelper.HasBatteryOrPowerRestrictionsInXml(cleanXml));
    }

    [Fact]
    public async Task EnsureBatteryRestrictionsDisabledAsync_ReRegistersTaskWhenRestrictionsPresent()
    {
        var origHook = StartupTaskHelper.RunProcessWithOutputHook;
        var origElevated = StartupTaskHelper.IsElevatedOverride;
        var commands = new List<string>();

        try
        {
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessWithOutputHook = (file, args, timeout) =>
            {
                commands.Add(args);
                if (args.Contains("/Query") && args.Contains("/XML"))
                {
                    string restrictiveXml = @"<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Settings>
    <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
  </Settings>
</Task>";
                    return Task.FromResult((0, restrictiveXml, string.Empty));
                }
                if (args.Contains("/Query"))
                {
                    return Task.FromResult((0, "SUCCESS", string.Empty));
                }
                if (args.Contains("/Create"))
                {
                    return Task.FromResult((0, "SUCCESS", string.Empty));
                }
                return Task.FromResult((0, string.Empty, string.Empty));
            };

            await StartupTaskHelper.EnsureBatteryRestrictionsDisabledAsync();

            Assert.Contains(commands, c => c.Contains("/Query") && c.Contains("/XML"));
            Assert.Contains(commands, c => c.Contains("/Create /TN \"FreeSnip\""));
        }
        finally
        {
            StartupTaskHelper.RunProcessWithOutputHook = origHook;
            StartupTaskHelper.IsElevatedOverride = origElevated;
        }
    }

    [Fact]
    public async Task EnsureBatteryRestrictionsDisabledAsync_NoOpWhenRestrictionsAlreadyDisabled()
    {
        var origHook = StartupTaskHelper.RunProcessWithOutputHook;
        var origElevated = StartupTaskHelper.IsElevatedOverride;
        var commands = new List<string>();

        try
        {
            StartupTaskHelper.IsElevatedOverride = true;
            string cleanXml = StartupTaskDefinition.Create(@"C:\SnapVox\snapvox.exe", "S-1-5-21-12345", elevated: true);
            StartupTaskHelper.RunProcessWithOutputHook = (file, args, timeout) =>
            {
                commands.Add(args);
                if (args.Contains("/Query") && args.Contains("/XML"))
                {
                    return Task.FromResult((0, cleanXml, string.Empty));
                }
                if (args.Contains("/Query"))
                {
                    return Task.FromResult((0, "SUCCESS", string.Empty));
                }
                return Task.FromResult((0, string.Empty, string.Empty));
            };

            await StartupTaskHelper.EnsureBatteryRestrictionsDisabledAsync();

            Assert.Contains(commands, c => c.Contains("/Query") && c.Contains("/XML"));
            Assert.DoesNotContain(commands, c => c.Contains("/Create"));
        }
        finally
        {
            StartupTaskHelper.RunProcessWithOutputHook = origHook;
            StartupTaskHelper.IsElevatedOverride = origElevated;
        }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FreeSnipUpgradeTests_" + Guid.NewGuid().ToString("N"));
        public string Settings => Path.Combine(_root, "snapvox.ini");
        public string BackupRoot => Path.Combine(_root, "recovery");
        public string ConfigFolder => Path.Combine(_root, "config");
        public string InstallFolder => Path.Combine(_root, "install");

        public TestFiles()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(ConfigFolder);
            Directory.CreateDirectory(InstallFolder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}

