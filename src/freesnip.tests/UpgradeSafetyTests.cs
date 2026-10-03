using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;
using freesnip.foundation.core;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.foundation.IniFile;
using freesnip.helpers;

namespace freesnip.tests;

public class UpgradeSafetyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\FreeSnip\FreeSnip.exe", true)]
    [InlineData(@"C:\Program Files\FreeSnip\Uninstall.exe", true)]
    [InlineData(@"C:\Program Files\OtherApp\Uninstall.exe", false)]
    [InlineData(@"C:\Program Files\FreeSnipOther\Uninstall.exe", false)]
    [InlineData(@"C:\Program Files\FreeSnip\..\OtherApp\Uninstall.exe", false)]
    [InlineData(@"C:\Users\User\Downloads\FreeSnip.exe", false)]
    public void ProcessOwnership_RequiresActualInstallDirectory(string executable, bool expected)
        => Assert.Equal(expected, StartupTaskHelper.IsInstalledExecutable(executable, @"C:\Program Files\FreeSnip"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_HasNoBatteryIdleNetworkOrTimeLimitRestrictions(bool elevated)
    {
        string executable = @"C:\Apps & Tools\FreeSnip.exe";
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
            key.SetValue("freesnip", "\"C:\\dummy\\freesnip.exe\" --autorun");
            key.SetValue("FreeSnip", "\"C:\\dummy\\FreeSnip.exe\" --autorun");
        }

        try
        {
            StartupTaskHelper.PurgeAllRunKeys();
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false);
            Assert.Null(key?.GetValue("freesnip"));
            Assert.Null(key?.GetValue("FreeSnip"));
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
            key?.DeleteValue("freesnip", false);
            key?.DeleteValue("FreeSnip", false);
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
            key.SetValue("freesnip", "\"C:\\dummy\\freesnip.exe\" --autorun");
            key.SetValue("FreeSnip", "\"C:\\dummy\\FreeSnip.exe\" --autorun");
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
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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
            StartupTaskHelper.IsElevatedOverride = origElevated;
            StartupTaskHelper.RunProcessHook = origHook;

            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
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

        string cleanXml = StartupTaskDefinition.Create(@"C:\FreeSnip\freesnip.exe", "S-1-5-21-12345", elevated: true);
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
            string cleanXml = StartupTaskDefinition.Create(@"C:\FreeSnip\freesnip.exe", "S-1-5-21-12345", elevated: true);
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

    [Fact]
    public async Task RestoreStartupAfterInstallAsync_WhenUpgradingFromSnapVox_MigratesSettingsAndPreservesElevated()
    {
        using var files = new TestFiles();
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;

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

            string legacyIniPath = Path.Combine(files.InstallFolder, "snapvox.ini");
            await File.WriteAllTextAsync(legacyIniPath, "[SnapVox]\r\nRunAsAdministratorOnStartup=true\r\nRegionHotkey=Ctrl + Shift + S\r\n");

            string newIniPath = Path.Combine(files.ConfigFolder, "freesnip.ini");
            Assert.False(File.Exists(newIniPath));

            await StartupTaskHelper.RestoreStartupAfterInstallAsync(keepUserSettings: true, hadElevatedStartup: false);

            Assert.Contains(commands, c => c.Contains("/Create /TN \"FreeSnip\""));
            Assert.True(File.Exists(newIniPath));

            string savedIni = await File.ReadAllTextAsync(newIniPath);
            Assert.Contains("RunAsAdministratorOnStartup=True", savedIni, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RegionHotkey=Ctrl + Shift + S", savedIni, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[Core]", savedIni, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StartupTaskHelper.ConfigurationFolder = origConfig;
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.IsElevatedOverride = origElevated;
            StartupTaskHelper.RunProcessHook = origHook;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Upgrade persistence: original-user identity, deterministic selection, exact restore, ownership
    // ---------------------------------------------------------------------------------------------

    private const string UserASid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static UpgradeUserContext CreateUserContext(TestFiles files, string name, string sid, bool hadTask = false)
    {
        string appData = Path.Combine(files.Root, name, "AppData", "Roaming");
        string local = Path.Combine(files.Root, name, "AppData", "Local");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(local);
        return new UpgradeUserContext(sid, appData, local, hadTask, DateTime.UtcNow);
    }

    private static string WriteIni(string path, string content, bool bom = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(bom));
        return path;
    }

    private static string CurrentSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    [Fact]
    public async Task Yes_PreservesExactIniBytes_AndCustomHotkeys()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath,
                "; user file\r\n[Core]\r\nRegionHotkey=Ctrl + Alt + Q\r\nNotificationOverlayDurationMs = 500\r\nRunAsAdministratorOnStartup=False\r\n\r\n[Editor]\r\nLastTextSize=37\r\n", bom: true);
            byte[] original = await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath);

            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            Assert.Equal(SettingsCandidateOrigin.CanonicalUserProfile, txn.Selection.Selected!.Origin);
            File.Delete(ctx.CanonicalSettingsPath);

            await txn.RestoreAsync();
            await txn.VerifyAsync(expectedRunAsAdministrator: false);
            Assert.Equal(original, await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath));
            Assert.Contains("RegionHotkey=Ctrl + Alt + Q", await File.ReadAllTextAsync(ctx.CanonicalSettingsPath));

            txn.Commit();
            Assert.False(Directory.Exists(txn.BackupFolder));
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public async Task Yes_PreservesRunAsAdministratorOnStartupTrue_WithoutRewritingSettings()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;
        var commands = new List<string>();
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessHook = (file, args, timeout) => { commands.Add(args); return Task.FromResult(0); };

            var ctx = CreateUserContext(files, "userA", CurrentSid());
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRunAsAdministratorOnStartup=True\r\nRegionHotkey=Alt + A\r\n");
            byte[] original = await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath);

            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            Assert.True(txn.SourceRequestsAdminStartup);
            File.Delete(ctx.CanonicalSettingsPath);
            await txn.RestoreAsync();

            var result = await StartupTaskHelper.RestoreStartupForUpgradeAsync(ctx, hadElevatedStartup: false);
            Assert.True(result.RunAsAdministratorOnStartup);
            Assert.False(result.SettingsFileModified);
            await txn.VerifyAsync(result.RunAsAdministratorOnStartup);

            Assert.Contains(commands, c => c.Contains("/Create /TN \"FreeSnip\""));
            Assert.Equal(original, await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath));
            Assert.True(IniConfig.GetIniSection<CoreConfiguration>(allowSave: false).RunAsAdministratorOnStartup);
        }
        finally
        {
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.IsElevatedOverride = origElevated;
            StartupTaskHelper.RunProcessHook = origHook;
        }
    }

    [Fact]
    public void CanonicalOriginalUserIni_BeatsProgramFilesTemplate()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=Ctrl + Q\r\n");
            var template = new System.Text.StringBuilder("[Core]\r\n");
            for (int i = 0; i < 50; i++) template.Append("Key").Append(i).Append("=v\r\n");
            WriteIni(Path.Combine(files.InstallFolder, "freesnip.ini"), template.ToString());

            var selection = SettingsCandidateSelector.Select(StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            Assert.Equal(Path.GetFullPath(ctx.CanonicalSettingsPath), selection.Selected!.Path);
            Assert.Equal(SettingsCandidateOrigin.CanonicalUserProfile, selection.Selected.Origin);
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public void HeaderOnlyTemplate_WithZeroKeyValues_CannotWin()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            const string stub = "; FreeSnip configuration\r\n[Core]\r\n# no values\r\n[FreeSnip]\r\n\r\n";
            Assert.Equal(0, SettingsCandidateSelector.CountValidIniKeys(stub));
            Assert.Equal(2, SettingsCandidateSelector.CountValidIniKeys("[Core]\r\nA=1\r\n; B=2\r\n[X]\r\nC = 3\r\nnot a pair\r\n"));

            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath, stub);
            WriteIni(Path.Combine(files.InstallFolder, "freesnip.ini"), stub);
            Assert.Null(SettingsCandidateSelector.Select(StartupTaskHelper.GetClassifiedSettingsCandidates(ctx)).Selected);

            string legacy = WriteIni(Path.Combine(ctx.AppData, "SnapVox", "snapvox.ini"), "[SnapVox]\r\nRegionHotkey=Ctrl + Shift + S\r\n");
            var selection = SettingsCandidateSelector.Select(StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            Assert.Equal(Path.GetFullPath(legacy), selection.Selected!.Path);
            Assert.Contains(selection.Evaluated, e => e.Origin == SettingsCandidateOrigin.CanonicalUserProfile && e.Exists && !e.Eligible);
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public async Task ValidLegacyUserIni_BeatsProgramFilesIni_AndIsMigratedToCanonical()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            string legacy = WriteIni(Path.Combine(ctx.LocalAppData, "SnapVox", "snapvox.ini"), "[SnapVox]\r\nRegionHotkey=Ctrl + Shift + S\r\nRunAsAdministratorOnStartup=true\r\n");
            var big = new System.Text.StringBuilder("[Core]\r\n");
            for (int i = 0; i < 40; i++) big.Append("Key").Append(i).Append("=v\r\n");
            WriteIni(Path.Combine(files.InstallFolder, "freesnip.ini"), big.ToString());

            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            Assert.Equal(Path.GetFullPath(legacy), txn.Selection.Selected!.Path);
            Assert.Equal(SettingsCandidateOrigin.LegacyUserProfile, txn.Selection.Selected.Origin);
            Assert.True(txn.SourceRequestsAdminStartup);

            await txn.RestoreAsync();
            await txn.VerifyAsync(expectedRunAsAdministrator: true);
            string restored = await File.ReadAllTextAsync(ctx.CanonicalSettingsPath);
            Assert.Contains("[Core]", restored);
            Assert.Contains("RegionHotkey=Ctrl + Shift + S", restored);
            Assert.DoesNotContain("Key39", restored);
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public async Task OriginalUserAContext_RemainsAuthoritative_WhenWorkerIdentityIsUserB()
    {
        using var files = new TestFiles();
        string origConfig = StartupTaskHelper.ConfigurationFolder;
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var userA = CreateUserContext(files, "userA", UserASid, hadTask: true);
            string staging = Path.Combine(files.Root, "Staging_1234");
            string manifest = await userA.WriteManifestAsync(staging);

            // Elevated worker runs as user B: its own folders hold a different, valid configuration.
            string userBConfig = Path.Combine(files.Root, "userB", "AppData", "Roaming", "FreeSnip");
            StartupTaskHelper.ConfigurationFolder = userBConfig;
            WriteIni(Path.Combine(userBConfig, "freesnip.ini"), "[Core]\r\nRegionHotkey=ADMIN B\r\n");
            WriteIni(userA.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=USER A\r\n");

            var loaded = UpgradeUserContext.LoadManifest(manifest, staging, DateTime.UtcNow);
            Assert.Equal(UserASid, loaded.UserSid);
            Assert.Equal(userA.CanonicalSettingsPath, loaded.CanonicalSettingsPath);
            Assert.Equal(userA.UpgradeBackupsRoot, loaded.UpgradeBackupsRoot);
            Assert.True(loaded.HadElevatedStartupTask);

            var candidates = StartupTaskHelper.GetClassifiedSettingsCandidates(loaded);
            Assert.DoesNotContain(candidates, c => c.Path.StartsWith(userBConfig, StringComparison.OrdinalIgnoreCase));
            var txn = await UpgradeSettingsTransaction.PrepareAsync(loaded, candidates);
            Assert.Equal(Path.GetFullPath(userA.CanonicalSettingsPath), txn.Selection.Selected!.Path);
            Assert.StartsWith(userA.LocalAppData, txn.BackupFolder!, StringComparison.OrdinalIgnoreCase);

            // Manifest validation: wrong folder, tampering and staleness are rejected.
            Assert.Throws<InvalidDataException>(() => UpgradeUserContext.LoadManifest(manifest, files.Root, DateTime.UtcNow));
            Assert.Throws<InvalidDataException>(() => UpgradeUserContext.LoadManifest(manifest, staging, DateTime.UtcNow.AddDays(2)));
            await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace(UserASid, "not-a-sid"));
            Assert.Throws<InvalidDataException>(() => UpgradeUserContext.LoadManifest(manifest, staging, DateTime.UtcNow));
        }
        finally
        {
            StartupTaskHelper.ConfigurationFolder = origConfig;
            StartupTaskHelper.InstallFolder = origInstall;
        }
    }

    [Fact]
    public async Task ElevatedStartupTask_IsCreatedForUserASid_NotWorkerUserB()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        bool? origElevated = StartupTaskHelper.IsElevatedOverride;
        var origHook = StartupTaskHelper.RunProcessHook;
        var definitions = new List<string>();
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            StartupTaskHelper.IsElevatedOverride = true;
            StartupTaskHelper.RunProcessHook = (file, args, timeout) =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(args, "/XML \"([^\"]+)\"");
                if (match.Success) definitions.Add(File.ReadAllText(match.Groups[1].Value));
                return Task.FromResult(0);
            };

            var ctx = CreateUserContext(files, "userA", UserASid, hadTask: true);
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRunAsAdministratorOnStartup=False\r\nRegionHotkey=Alt + A\r\n");
            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            await txn.RestoreAsync();

            var result = await StartupTaskHelper.RestoreStartupForUpgradeAsync(ctx, hadElevatedStartup: true);
            Assert.True(result.RunAsAdministratorOnStartup);
            Assert.True(result.SettingsFileModified);
            await txn.VerifyAsync(result.RunAsAdministratorOnStartup);

            string xml = Assert.Single(definitions);
            XNamespace ns = StartupTaskDefinition.Namespace;
            string owner = XDocument.Parse(xml).Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value;
            Assert.Equal(UserASid, owner);
            Assert.NotEqual(CurrentSid(), owner);

            string restored = await File.ReadAllTextAsync(ctx.CanonicalSettingsPath);
            Assert.Contains("RunAsAdministratorOnStartup=True", restored);
            Assert.Contains("RegionHotkey=Alt + A", restored);
            Assert.Equal(UserASid, StartupTaskHelper.ResolveStartupTaskSid(UserASid));
            Assert.Equal(CurrentSid(), StartupTaskHelper.ResolveStartupTaskSid(null));
        }
        finally
        {
            StartupTaskHelper.InstallFolder = origInstall;
            StartupTaskHelper.IsElevatedOverride = origElevated;
            StartupTaskHelper.RunProcessHook = origHook;
        }
    }

    [Fact]
    public void No_StillPerformsCleanWipe()
    {
        var decision = DeploymentLifecycle.DecideUpgrade(isAutoUpdate: false, existingInstall: true, DialogResult.No);
        Assert.True(decision.CleanWipe);
        Assert.False(decision.KeepUserSettings);
        Assert.False(decision.Cancelled);

        var wipe = DeploymentLifecycle.ComputeCleanupDirectoryTargets(purgeUserArtifacts: true);
        Assert.Contains(DeploymentFootprint.RoamingAppDataFolder, wipe, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(DeploymentFootprint.LocalAppDataFolder, wipe, StringComparer.OrdinalIgnoreCase);

        var keep = DeploymentLifecycle.ComputeCleanupDirectoryTargets(purgeUserArtifacts: false);
        Assert.DoesNotContain(DeploymentFootprint.RoamingAppDataFolder, keep, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeploymentFootprint.LocalAppDataFolder, keep, StringComparer.OrdinalIgnoreCase);

        var yes = DeploymentLifecycle.DecideUpgrade(false, true, DialogResult.Yes);
        Assert.True(yes.KeepUserSettings);
        Assert.False(yes.CleanWipe);
        Assert.True(DeploymentLifecycle.DecideUpgrade(false, true, DialogResult.Cancel).Cancelled);
    }

    [Fact]
    public async Task AutoUpdate_PreservesSettings()
    {
        foreach (DialogResult? choice in new DialogResult?[] { null, DialogResult.No, DialogResult.Cancel })
        {
            var decision = DeploymentLifecycle.DecideUpgrade(isAutoUpdate: true, existingInstall: true, choice);
            Assert.True(decision.KeepUserSettings);
            Assert.False(decision.CleanWipe);
            Assert.False(decision.Cancelled);
        }

        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=Win + Shift + S\r\n");
            byte[] original = await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath);
            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            File.Delete(ctx.CanonicalSettingsPath);
            await txn.RestoreAsync();
            await txn.VerifyAsync(false);
            Assert.Equal(original, await File.ReadAllBytesAsync(ctx.CanonicalSettingsPath));
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public async Task FailedUpgradeRestore_RetainsRecoveryBackup()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=Alt + R\r\n");
            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));

            using (new FileStream(ctx.CanonicalSettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = await Record.ExceptionAsync(() => txn.RestoreAsync());
                Assert.True(error is IOException or UnauthorizedAccessException);
            }

            Assert.False(txn.IsVerified);
            Assert.Throws<InvalidOperationException>(() => txn.Commit());
            Assert.True(Directory.Exists(txn.BackupFolder));
            Assert.True(File.Exists(Path.Combine(txn.BackupFolder!, "settings_0.ini")));
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    [Fact]
    public async Task SuccessfulRestoreVerification_HappensBeforeBackupDeletion()
    {
        using var files = new TestFiles();
        string origInstall = StartupTaskHelper.InstallFolder;
        try
        {
            StartupTaskHelper.InstallFolder = files.InstallFolder;
            var ctx = CreateUserContext(files, "userA", UserASid);
            WriteIni(ctx.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=Alt + V\r\nRunAsAdministratorOnStartup=False\r\n");
            var txn = await UpgradeSettingsTransaction.PrepareAsync(ctx, StartupTaskHelper.GetClassifiedSettingsCandidates(ctx));
            await txn.RestoreAsync();

            Assert.Throws<InvalidOperationException>(() => txn.Commit());
            Assert.True(Directory.Exists(txn.BackupFolder));

            await File.WriteAllTextAsync(ctx.CanonicalSettingsPath, "[Core]\r\nRegionHotkey=DEFAULTED\r\nRunAsAdministratorOnStartup=False\r\n");
            await Assert.ThrowsAsync<IOException>(() => txn.VerifyAsync(false));
            Assert.Throws<InvalidOperationException>(() => txn.Commit());
            Assert.True(Directory.Exists(txn.BackupFolder));

            await txn.RestoreAsync();
            await txn.VerifyAsync(false);
            Assert.True(Directory.Exists(txn.BackupFolder));
            txn.Commit();
            Assert.True(txn.IsCommitted);
            Assert.False(Directory.Exists(txn.BackupFolder));
        }
        finally { StartupTaskHelper.InstallFolder = origInstall; }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FreeSnipUpgradeTests_" + Guid.NewGuid().ToString("N"));
        public string Root => _root;
        public string Settings => Path.Combine(_root, "freesnip.ini");
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

