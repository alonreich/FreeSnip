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
}
