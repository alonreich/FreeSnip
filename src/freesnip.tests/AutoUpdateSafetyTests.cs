using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.services;
using Xunit;

namespace freesnip.tests;

[Collection(IniConfigStateCollection.Name)]
public class AutoUpdateSafetyTests
{
    [Fact]
    public void CoreConfiguration_AutoUpdateChecks_DefaultsToTrue()
    {
        var config = new CoreConfiguration();
        config.Fill(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(config.AutoUpdateChecks);
    }

    [Fact]
    public void CoreConfiguration_WhenKeyMissingInIni_RetainsTrueDefault()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_Test_Config_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string iniPath = Path.Combine(tempDir, "freesnip.ini");
            File.WriteAllText(iniPath, "[Core]\r\nLanguage=en-US\r\nKeepBackup=true\r\n");

            IniConfig.IniDirectory = tempDir;
            IniConfig.Init("FreeSnip", "freesnip");
            var loaded = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);

            Assert.True(loaded.AutoUpdateChecks);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CoreConfiguration_WhenExplicitlyFalseInIni_LoadsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_Test_Config_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string iniPath = Path.Combine(tempDir, "freesnip.ini");
            File.WriteAllText(iniPath, "[Core]\r\nLanguage=en-US\r\nAutoUpdateChecks=false\r\n");

            IniConfig.IniDirectory = tempDir;
            IniConfig.Init("FreeSnip", "freesnip");
            var loaded = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);

            Assert.False(loaded.AutoUpdateChecks);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("v2026.10.02.1600", "2026.10.2.1600")]
    [InlineData("2026.10.02.1600", "2026.10.2.1600")]
    [InlineData("v1.2.3.4", "1.2.3.4")]
    [InlineData("1.0.0+hash123", "1.0.0")]
    [InlineData("v2.5.0-alpha", "2.5.0")]
    public void TryParseVersion_ParsesValidVersionsCorrectly(string input, string expected)
    {
        bool success = RuntimePathHelper.TryParseVersion(input, out Version parsed);
        Assert.True(success);
        Assert.Equal(Version.Parse(expected), parsed);
    }

    [Fact]
    public void TryParseVersion_Comparison_StrictlyGreaterIdentifiesUpdates()
    {
        Assert.True(RuntimePathHelper.TryParseVersion("v2026.10.02.1601", out Version remote));
        Assert.True(RuntimePathHelper.TryParseVersion("2026.10.02.1600", out Version local));
        Assert.True(remote.CompareTo(local) > 0);

        Assert.True(RuntimePathHelper.TryParseVersion("v2026.10.02.1600", out Version sameRemote));
        Assert.True(sameRemote.CompareTo(local) == 0);

        Assert.True(RuntimePathHelper.TryParseVersion("v2026.10.02.1559", out Version olderRemote));
        Assert.True(olderRemote.CompareTo(local) < 0);
    }

    [Theory]
    [InlineData("v2026.10.11.1015", "2026.10.7.935", true)]
    [InlineData("v2026.10.7.935", "2026.10.7.935", false)]
    [InlineData("v2026.10.7.900", "2026.10.7.935", false)]
    [InlineData("v3.0.1", "2026.10.7.935", true)]
    [InlineData("v3.0.2", "3.0.1", true)]
    [InlineData("v3.0.1", "3.0.2", false)]
    public void IsRemoteVersionNewer_DetectsNewerVersionsCorrectly(string remoteTag, string localVer, bool expected)
    {
        Assert.True(RuntimePathHelper.TryParseVersion(remoteTag, out Version remote));
        Assert.True(RuntimePathHelper.TryParseVersion(localVer, out Version local));
        Assert.Equal(expected, UpdateService.IsRemoteVersionNewer(remote, local));
    }

    [Fact]
    public void ExpectedAssetName_MatchesValidBinaryFlavor()
    {
        string assetName = UpdateService.GetExpectedAssetName();
        Assert.True(assetName == "FreeSnip.exe" || assetName == "FreeSnip_tesseract.exe");
    }

    [Fact]
    public void Sha256Verification_MatchesExpectedHash()
    {
        byte[] testBytes = Encoding.UTF8.GetBytes("FreeSnip update binary payload test");
        string expectedHash = Convert.ToHexString(SHA256.HashData(testBytes)).ToLowerInvariant();

        using var ms = new MemoryStream(testBytes);
        string computedHash = Convert.ToHexString(SHA256.HashData(ms)).ToLowerInvariant();

        Assert.Equal(expectedHash, computedHash);
        Assert.False(string.Equals(computedHash, "0000000000000000000000000000000000000000000000000000000000000000", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpectedDeltaAssetName_MatchesValidFlavor()
    {
        string deltaName = UpdateService.GetExpectedDeltaAssetName();
        Assert.True(deltaName == "FreeSnip_delta.zip" || deltaName == "FreeSnip_tesseract_delta.zip");
    }

    [Fact]
    public void RuntimePathHelper_InformationalVersionAndCommitSha_Accessible()
    {
        string infoVer = RuntimePathHelper.InformationalVersion;
        string commitSha = RuntimePathHelper.CommitSha;
        Assert.NotNull(infoVer);
        Assert.NotNull(commitSha);
    }

    [Fact]
    public void UpdateChoice_NotNow_HasDedicatedValue()
    {
        var choice = freesnip.forms.UpdateChoice.NotNow;
        Assert.NotEqual(freesnip.forms.UpdateChoice.UpdateNow, choice);
        Assert.NotEqual(freesnip.forms.UpdateChoice.SkipThisVersion, choice);
        Assert.NotEqual(freesnip.forms.UpdateChoice.Dismissed, choice);
    }

    [Theory]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, false, false, false, false)]
    public void DecideUpgrade_TableSafety(bool isAutoUpdate, bool existingInstall, bool expectedKeep, bool expectedWipe, bool expectedCancel)
    {
        var decision = freesnip.helpers.DeploymentLifecycle.DecideUpgrade(isAutoUpdate, existingInstall, null);
        Assert.Equal(expectedKeep, decision.KeepUserSettings);
        Assert.Equal(expectedWipe, decision.CleanWipe);
        Assert.Equal(expectedCancel, decision.Cancelled);
    }

    [Fact]
    public void UpdateChoice_NeverTellMeAgain_HasDedicatedValue()
    {
        var choice = freesnip.forms.UpdateChoice.NeverTellMeAgain;
        Assert.NotEqual(freesnip.forms.UpdateChoice.UpdateNow, choice);
        Assert.NotEqual(freesnip.forms.UpdateChoice.NotNow, choice);
        Assert.NotEqual(freesnip.forms.UpdateChoice.SkipThisVersion, choice);
        Assert.NotEqual(freesnip.forms.UpdateChoice.Dismissed, choice);
    }

    [Fact]
    public void PendingUpdate_SaveRetrieveClear_LifecycleWorksAccurately()
    {
        string fakeInstaller = Path.Combine(Path.GetTempPath(), "FreeSnip_FakeInstaller_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(fakeInstaller, "test executable stub");

        try
        {
            UpdateService.SavePendingUpdate(fakeInstaller, "v2026.10.15", "abcdef123456", false);
            var pending = UpdateService.GetPendingUpdate();
            Assert.NotNull(pending);
            Assert.Equal(fakeInstaller, pending.InstallerPath);
            Assert.Equal("v2026.10.15", pending.Tag);
            Assert.Equal("abcdef123456", pending.Sha256Hex);
            Assert.False(pending.IsDelta);

            UpdateService.ClearPendingUpdate();
            Assert.Null(UpdateService.GetPendingUpdate());
        }
        finally
        {
            if (File.Exists(fakeInstaller)) File.Delete(fakeInstaller);
            UpdateService.ClearPendingUpdate();
        }
    }

    [Fact]
    public void VerifyAuthenticodeSignature_NonExistentFile_ReturnsFalse()
    {
        Assert.False(UpdateService.VerifyAuthenticodeSignature(@"C:\NonExistent_Path_987654321\dummy.exe"));
    }

    [Fact]
    public void VerifyAuthenticodeSignature_UnsignedFile_FailsClosed()
    {
        string path = Path.Combine(Path.GetTempPath(), "freesnip_unsigned_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        try
        {
            Assert.False(UpdateService.VerifyAuthenticodeSignature(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLockVerifiedPayload_PathOutsideDownloadRoot_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), "freesnip_outside_" + Guid.NewGuid().ToString("N") + ".exe");
        byte[] bytes = Encoding.ASCII.GetBytes("payload");
        File.WriteAllBytes(path, bytes);
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Null(UpdateService.TryLockVerifiedPayload(path, hash, isDelta: true));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TryLockVerifiedPayload_HashMismatch_IsRejectedAndDiscarded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "FreeSnip_AutoUpdate", "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "FreeSnip_delta.zip");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("payload"));
        try
        {
            Assert.Null(UpdateService.TryLockVerifiedPayload(path, new string('0', 64), isDelta: true));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryLockVerifiedPayload_MatchingDelta_HoldsWriteLock()
    {
        string folder = Path.Combine(Path.GetTempPath(), "FreeSnip_AutoUpdate", "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "FreeSnip_delta.zip");
        byte[] bytes = Encoding.ASCII.GetBytes("payload");
        File.WriteAllBytes(path, bytes);
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using FileStream locked = UpdateService.TryLockVerifiedPayload(path, hash, isDelta: true);
            Assert.NotNull(locked);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, new byte[] { 1 }));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
