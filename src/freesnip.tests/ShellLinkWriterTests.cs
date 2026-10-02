using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.Interop;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests;

public class ShellLinkWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ShellLinkWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_ShellLinkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Create_StandardShortcut_CreatesValidLnkFileAndResolvesProperties()
    {
        string shortcutPath = Path.Combine(_tempDir, "TestApp.lnk");
        string targetPath = @"C:\Windows\System32\notepad.exe";
        string workingDir = @"C:\Windows\System32";
        string iconLocation = @"C:\Windows\System32\notepad.exe,0";
        string description = "Test Notepad Shortcut";
        string arguments = "--test-arg";

        ShellLinkWriter.Create(shortcutPath, targetPath, workingDir, iconLocation, description, arguments);

        Assert.True(File.Exists(shortcutPath));
        var (readTarget, readDir, readArgs, readDesc, readIcon, readIconIdx) = ShellLinkWriter.Read(shortcutPath);

        Assert.Equal(targetPath, readTarget, ignoreCase: true);
        Assert.Equal(workingDir, readDir, ignoreCase: true);
        Assert.Equal(arguments, readArgs);
        Assert.Equal(description, readDesc);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", readIcon, ignoreCase: true);
        Assert.Equal(0, readIconIdx);
    }

    [Fact]
    public void Create_UnicodeAndSpacesInPaths_ResolvesAccurately()
    {
        string targetDir = Path.Combine(_tempDir, "Folder With Spaces & Symbols (Test) \u4e2d\u6587 \u05e2\u05d1\u05e8\u05d9\u05ea");
        Directory.CreateDirectory(targetDir);
        string targetFile = Path.Combine(targetDir, "App \u00e9l\u00e8ve.exe");
        File.WriteAllText(targetFile, "dummy");

        string shortcutPath = Path.Combine(_tempDir, "SubDir", "Unicode \u4e2d\u6587 Shortcut.lnk");
        string workingDir = targetDir;
        string iconLocation = $"\"{targetFile}\", 2";
        string description = "Unicode Description \u4e2d\u6587 \u05e2\u05d1\u05e8\u05d9\u05ea";
        string arguments = "--input \"C:\\Path With Spaces\\file.txt\"";

        ShellLinkWriter.Create(shortcutPath, targetFile, workingDir, iconLocation, description, arguments);

        Assert.True(File.Exists(shortcutPath));
        var (readTarget, readDir, readArgs, readDesc, readIcon, readIconIdx) = ShellLinkWriter.Read(shortcutPath);

        Assert.Equal(targetFile, readTarget, ignoreCase: true);
        Assert.Equal(workingDir, readDir, ignoreCase: true);
        Assert.Equal(arguments, readArgs);
        Assert.Equal(description, readDesc);
        Assert.Equal(targetFile, readIcon, ignoreCase: true);
        Assert.Equal(2, readIconIdx);
    }

    [Fact]
    public void Create_StartMenuProgramsDirectory_Succeeds()
    {
        string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrEmpty(startMenuPrograms) || !Directory.Exists(startMenuPrograms))
        {
            startMenuPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
        }

        string shortcutPath = Path.Combine(startMenuPrograms, "FreeSnip_UnitTest_" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            string targetPath = @"C:\Windows\System32\cmd.exe";
            ShellLinkWriter.Create(shortcutPath, targetPath, @"C:\Windows\System32", @"C:\Windows\System32\cmd.exe,0", "FreeSnip StartMenu Test");

            Assert.True(File.Exists(shortcutPath));
            var (readTarget, _, _, readDesc, _, _) = ShellLinkWriter.Read(shortcutPath);
            Assert.Equal(targetPath, readTarget, ignoreCase: true);
            Assert.Equal("FreeSnip StartMenu Test", readDesc);
        }
        finally
        {
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); } catch { }
            }
        }
    }

    [Fact]
    public void Create_DesktopDirectory_Succeeds()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string shortcutPath = Path.Combine(desktop, "FreeSnip_DesktopTest_" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            string targetPath = @"C:\Windows\System32\calc.exe";
            ShellLinkWriter.Create(shortcutPath, targetPath, @"C:\Windows", null, "FreeSnip Desktop Test");

            Assert.True(File.Exists(shortcutPath));
            var (readTarget, _, _, readDesc, _, _) = ShellLinkWriter.Read(shortcutPath);
            Assert.Equal(targetPath, readTarget, ignoreCase: true);
            Assert.Equal("FreeSnip Desktop Test", readDesc);
        }
        finally
        {
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); } catch { }
            }
        }
    }

    [Fact]
    public void Create_PublicDesktopDirectory_IfPermitted_Succeeds()
    {
        string publicDir = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        string publicDesktop = Path.Combine(publicDir, "Desktop");

        if (!Directory.Exists(publicDesktop)) return;

        string shortcutPath = Path.Combine(publicDesktop, "FreeSnip_PublicDesktopTest_" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            string targetPath = @"C:\Windows\System32\notepad.exe";
            ShellLinkWriter.Create(shortcutPath, targetPath, null, null, "Public Desktop Test");
            Assert.True(File.Exists(shortcutPath));
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Tools\app.exe,0", @"C:\Tools\app.exe", 0)]
    [InlineData(@"C:\Tools\app.exe, 3", @"C:\Tools\app.exe", 3)]
    [InlineData(@"C:\Tools\app.exe", @"C:\Tools\app.exe", 0)]
    [InlineData("\"C:\\Tools With Spaces\\app.exe\", 5", @"C:\Tools With Spaces\app.exe", 5)]
    public void Create_IconLocationParsing_HandlesVariousFormats(string inputIcon, string expectedIconPath, int expectedIconIdx)
    {
        string shortcutPath = Path.Combine(_tempDir, $"IconTest_{Guid.NewGuid():N}.lnk");
        ShellLinkWriter.Create(shortcutPath, @"C:\Windows\System32\cmd.exe", iconLocation: inputIcon);

        Assert.True(File.Exists(shortcutPath));
        var (_, _, _, _, readIcon, readIconIdx) = ShellLinkWriter.Read(shortcutPath);

        Assert.Equal(expectedIconPath, readIcon, ignoreCase: true);
        Assert.Equal(expectedIconIdx, readIconIdx);
    }

    [Fact]
    public void Create_SubMillisecondPerformance_ExecutesRapidly()
    {
        string warmupPath = Path.Combine(_tempDir, "warmup.lnk");
        ShellLinkWriter.Create(warmupPath, @"C:\Windows\System32\notepad.exe");

        int iterations = 20;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            string path = Path.Combine(_tempDir, $"bench_{i}.lnk");
            ShellLinkWriter.Create(path, @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32", @"C:\Windows\System32\notepad.exe,0", "Bench", "--arg");
        }
        sw.Stop();

        double avgMilliseconds = sw.Elapsed.TotalMilliseconds / iterations;
        Assert.True(avgMilliseconds < 10.0, $"Average execution time {avgMilliseconds:F2}ms exceeds threshold.");
    }

    [Fact]
    public async Task CreateAsync_CancelsProperly_WhenTokenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        string shortcutPath = Path.Combine(_tempDir, "canceled.lnk");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ShellLinkWriter.CreateAsync(shortcutPath, @"C:\Windows\System32\notepad.exe", null, null, null, cts.Token);
        });
    }

    [Fact]
    public void Create_ThrowsArgumentException_WhenPathNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => ShellLinkWriter.Create(null!, @"C:\Windows\notepad.exe"));
        Assert.Throws<ArgumentException>(() => ShellLinkWriter.Create("", @"C:\Windows\notepad.exe"));
        Assert.Throws<ArgumentException>(() => ShellLinkWriter.Create(Path.Combine(_tempDir, "valid.lnk"), null!));
        Assert.Throws<ArgumentException>(() => ShellLinkWriter.Create(Path.Combine(_tempDir, "valid.lnk"), ""));
    }

    [Fact]
    public void Create_CreatesParentDirectoryIfMissing()
    {
        string deeplyNested = Path.Combine(_tempDir, "Level1", "Level2", "Level3", "Nested.lnk");
        ShellLinkWriter.Create(deeplyNested, @"C:\Windows\System32\notepad.exe");

        Assert.True(File.Exists(deeplyNested));
        Assert.True(Directory.Exists(Path.GetDirectoryName(deeplyNested)));
    }
}
