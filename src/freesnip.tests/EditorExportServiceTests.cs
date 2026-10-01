using System;
using System.IO;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using freesnip.editor.Services;
using freesnip.foundation.core;
using freesnip.foundation.core.Enums;
using Xunit;

namespace freesnip.tests
{
    public class EditorExportServiceTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void SanitizeFileName_NullOrWhitespace_ReturnsNull(string? input)
        {
            var result = EditorExportService.SanitizeFileName(input);
            Assert.Null(result);
        }

        [Fact]
        public void SanitizeFileName_StripsInvalidCharsAndCollapsesSeparators()
        {
            string raw = "My  Document: Version*1 / Final? <Draft>";
            string result = EditorExportService.SanitizeFileName(raw)!;

            Assert.Equal("My_Document_Version_1_Final_Draft", result);
            Assert.DoesNotContain("__", result);
            Assert.False(result.StartsWith("_"));
            Assert.False(result.EndsWith("_"));
        }

        [Fact]
        public void SanitizeFileName_ClampsMaxLength()
        {
            string raw = new string('A', 50);
            string result = EditorExportService.SanitizeFileName(raw, maxLength: 40)!;

            Assert.Equal(40, result.Length);
            Assert.Equal(new string('A', 40), result);
        }

        [Fact]
        public void GenerateDownloadFileName_ProducesCorrectExtensionsAndFormat()
        {
            var time = new DateTime(2026, 9, 8, 14, 30, 45, 123);

            string jpgName = EditorExportService.GenerateDownloadFileName("Report?2026", allowPng: false, timestamp: time);
            Assert.Equal("Report_2026_2026-09-08_14-30-45_123.jpg", jpgName);

            string pngName = EditorExportService.GenerateDownloadFileName("Report?2026", allowPng: true, timestamp: time);
            Assert.Equal("Report_2026_2026-09-08_14-30-45_123.png", pngName);

            string defaultPrefix = EditorExportService.GenerateDownloadFileName(null, allowPng: false, timestamp: time);
            Assert.Equal("Capture_2026-09-08_14-30-45_123.jpg", defaultPrefix);
        }

        [Fact]
        public void GenerateClipboardBackupFileName_ProducesSpaceFormattedDate()
        {
            var time = new DateTime(2026, 9, 8, 14, 30, 45, 123);

            string jpgName = EditorExportService.GenerateClipboardBackupFileName(allowPng: false, timestamp: time);
            Assert.Equal("Capture_2026-09-08 14_30_45_123.jpg", jpgName);

            string pngName = EditorExportService.GenerateClipboardBackupFileName(allowPng: true, timestamp: time);
            Assert.Equal("Capture_2026-09-08 14_30_45_123.png", pngName);
        }

        [Fact]
        public async Task SaveImageAsync_EncodesValidJpegAndPngSignatures()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_ExportTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var image = new Image<Rgba32>(32, 32);
                string jpgPath = Path.Combine(tempDir, "test.jpg");
                string pngPath = Path.Combine(tempDir, "test.png");

                await EditorExportService.SaveImageAsync(image, jpgPath, allowPng: false, jpegQuality: 90);
                await EditorExportService.SaveImageAsync(image, pngPath, allowPng: true);

                Assert.True(File.Exists(jpgPath));
                Assert.True(File.Exists(pngPath));

                byte[] jpgBytes = await File.ReadAllBytesAsync(jpgPath);
                Assert.True(jpgBytes.Length > 2);
                Assert.Equal(0xFF, jpgBytes[0]);
                Assert.Equal(0xD8, jpgBytes[1]);

                byte[] pngBytes = await File.ReadAllBytesAsync(pngPath);
                Assert.True(pngBytes.Length > 4);
                Assert.Equal(0x89, pngBytes[0]);
                Assert.Equal(0x50, pngBytes[1]);
                Assert.Equal(0x4E, pngBytes[2]);
                Assert.Equal(0x47, pngBytes[3]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void EncodeImage_ReturnsExpectedEncodedBytes()
        {
            using var image = new Image<Rgba32>(16, 16);

            byte[] jpg = EditorExportService.EncodeImage(image, allowPng: false, jpegQuality: 85);
            Assert.NotEmpty(jpg);
            Assert.Equal(0xFF, jpg[0]);
            Assert.Equal(0xD8, jpg[1]);

            byte[] png = EditorExportService.EncodeImage(image, allowPng: true);
            Assert.NotEmpty(png);
            Assert.Equal(0x89, png[0]);
            Assert.Equal(0x50, png[1]);
        }

        [Fact]
        public async Task SaveToHistoryBackupAsync_RespectsKeepBackupFlag()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_BackupTests_" + Guid.NewGuid().ToString("N"));

            try
            {
                using var image = new Image<Rgba32>(16, 16);

                bool disabledResult = await EditorExportService.SaveToHistoryBackupAsync(
                    "backup1.jpg", image, keepBackup: false, allowPng: false, jpegQuality: 90, customBackupDir: tempDir);
                Assert.False(disabledResult);
                Assert.False(File.Exists(Path.Combine(tempDir, "backup1.jpg")));

                bool enabledResult = await EditorExportService.SaveToHistoryBackupAsync(
                    "backup2.jpg", image, keepBackup: true, allowPng: false, jpegQuality: 90, customBackupDir: tempDir);
                Assert.True(enabledResult);
                Assert.True(File.Exists(Path.Combine(tempDir, "backup2.jpg")));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task ResolveDownloadTargetAsync_FollowsPrecedenceChain()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_TargetTests_" + Guid.NewGuid().ToString("N"));
            string userDir = Path.Combine(tempDir, "UserConfigured");
            string defaultDir = Path.Combine(tempDir, "ShellDownloads");
            string pickedDir = Path.Combine(tempDir, "UserPicked");

            Directory.CreateDirectory(userDir);
            Directory.CreateDirectory(defaultDir);
            Directory.CreateDirectory(pickedDir);

            try
            {
                var t1 = await EditorExportService.ResolveDownloadTargetAsync(
                    userDownloadPath: userDir,
                    folderPicker: null,
                    customDefaultPath: defaultDir);
                Assert.Equal(userDir, t1.Path);
                Assert.True(t1.IsDownloadsFolder);

                var t2 = await EditorExportService.ResolveDownloadTargetAsync(
                    userDownloadPath: null,
                    folderPicker: null,
                    customDefaultPath: defaultDir);
                Assert.Equal(defaultDir, t2.Path);
                Assert.True(t2.IsDownloadsFolder);

                string? configuredPath = null;
                var t3 = await EditorExportService.ResolveDownloadTargetAsync(
                    userDownloadPath: "C:\\NonExistent_Dir_12345",
                    folderPicker: () => Task.FromResult<string?>(pickedDir),
                    onUserPathConfigured: p => configuredPath = p,
                    customDefaultPath: "C:\\NonExistent_Dir_67890");
                Assert.Equal(pickedDir, t3.Path);
                Assert.True(t3.IsDownloadsFolder);
                Assert.Equal(pickedDir, configuredPath);

                var t4 = await EditorExportService.ResolveDownloadTargetAsync(
                    userDownloadPath: null,
                    folderPicker: () => Task.FromResult<string?>(null),
                    customDefaultPath: "C:\\NonExistent_Dir_67890");
                Assert.Equal(Path.Combine(Path.GetTempPath(), "FreeSnip"), t4.Path);
                Assert.False(t4.IsDownloadsFolder);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        // ── PNG-default regression tests ──────────────────────────────────────

        /// <summary>
        /// Default invocation (allowPng not explicitly specified) of GenerateDownloadFileName must produce a .png extension.
        /// </summary>
        [Fact]
        public void GenerateDownloadFileName_DefaultProducesPng()
        {
            var time = new DateTime(2026, 9, 8, 14, 30, 45, 123);
            string name = EditorExportService.GenerateDownloadFileName("Screenshot", timestamp: time);
            Assert.EndsWith(".png", name, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Screenshot_2026-09-08_14-30-45_123.png", name);
        }

        /// <summary>
        /// Default invocation (allowPng not explicitly specified) of GenerateClipboardBackupFileName must produce a .png extension.
        /// </summary>
        [Fact]
        public void GenerateClipboardBackupFileName_DefaultProducesPng()
        {
            var time = new DateTime(2026, 9, 8, 14, 30, 45, 123);
            string name = EditorExportService.GenerateClipboardBackupFileName(timestamp: time);
            Assert.EndsWith(".png", name, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Capture_2026-09-08 14_30_45_123.png", name);
        }

        /// <summary>
        /// SaveImageAsync with default parameters (allowPng = true) must write a file whose first four bytes are the
        /// canonical PNG magic: 0x89 P N G (0x89, 0x50, 0x4E, 0x47).
        /// </summary>
        [Fact]
        public async Task SaveImageAsync_DefaultPngHasValidMagicHeader()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_PngDefaultTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var image = new Image<Rgba32>(16, 16);
                string pngPath = Path.Combine(tempDir, "default_output.png");

                await EditorExportService.SaveImageAsync(image, pngPath);

                Assert.True(File.Exists(pngPath));

                byte[] bytes = await File.ReadAllBytesAsync(pngPath);
                Assert.True(bytes.Length > 4, "PNG file must be longer than 4 bytes.");

                // PNG magic bytes: 0x89 'P' 'N' 'G'
                Assert.Equal(0x89, bytes[0]);
                Assert.Equal(0x50, bytes[1]);
                Assert.Equal(0x4E, bytes[2]);
                Assert.Equal(0x47, bytes[3]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// SaveImageAsync with PNG must preserve 32-bit ARGB alpha transparency intact without flattening transparent pixels to black.
        /// </summary>
        [Fact]
        public async Task SaveImageAsync_PngPreservesAlphaTransparency()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_AlphaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var image = new Image<Rgba32>(4, 4);
                // Pixel 0,0: 100% transparent
                image[0, 0] = new Rgba32(0, 0, 0, 0);
                // Pixel 1,1: 50% semi-transparent red
                image[1, 1] = new Rgba32(255, 0, 0, 128);
                // Pixel 2,2: 100% opaque green
                image[2, 2] = new Rgba32(0, 255, 0, 255);

                string pngPath = Path.Combine(tempDir, "alpha_test.png");
                await EditorExportService.SaveImageAsync(image, pngPath);

                Assert.True(File.Exists(pngPath));

                using var loaded = await Image.LoadAsync<Rgba32>(pngPath);
                Assert.Equal(0, loaded[0, 0].A);
                Assert.Equal(128, loaded[1, 1].A);
                Assert.Equal(255, loaded[2, 2].A);
                Assert.Equal(255, loaded[1, 1].R);
                Assert.Equal(255, loaded[2, 2].G);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// SaveImageAsync with explicit JPEG configuration (allowPng: false) must write valid JPEG magic header (0xFF, 0xD8).
        /// </summary>
        [Fact]
        public async Task SaveImageAsync_ExplicitJpegProducesValidJpegMagicHeader()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_JpegTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var image = new Image<Rgba32>(8, 8);
                string jpgPath = Path.Combine(tempDir, "explicit_output.jpg");

                await EditorExportService.SaveImageAsync(image, jpgPath, allowPng: false, jpegQuality: 100);

                Assert.True(File.Exists(jpgPath));

                byte[] bytes = await File.ReadAllBytesAsync(jpgPath);
                Assert.True(bytes.Length > 2, "JPEG file must be longer than 2 bytes.");

                // JPEG magic bytes: 0xFF 0xD8
                Assert.Equal(0xFF, bytes[0]);
                Assert.Equal(0xD8, bytes[1]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// CoreConfiguration must default to OutputFileAllowPng = true, OutputFileFormat = OutputFormat.png, and OutputFileJpegQuality = 100.
        /// </summary>
        [Fact]
        public void CoreConfiguration_DefaultsToLosslessPngAnd100Quality()
        {
            var config = new CoreConfiguration();
            Assert.True(config.OutputFileAllowPng);
            Assert.Equal(OutputFormat.png, config.OutputFileFormat);
            Assert.Equal(100, config.OutputFileJpegQuality);
        }

        /// <summary>
        /// CoreConfiguration.AfterLoad must synchronize OutputFileAllowPng to true when OutputFileFormat == png and enforce 100% quality.
        /// </summary>
        [Fact]
        public void CoreConfiguration_AfterLoad_SynchronizesPngAndEnforces100Quality()
        {
            var config = new CoreConfiguration
            {
                OutputFileFormat = OutputFormat.png,
                OutputFileAllowPng = false,
                OutputFileJpegQuality = 75
            };

            config.AfterLoad();

            Assert.True(config.OutputFileAllowPng);
            Assert.Equal(OutputFormat.png, config.OutputFileFormat);
            Assert.Equal(100, config.OutputFileJpegQuality);
        }

        /// <summary>
        /// CoreConfiguration.AfterLoad must synchronize OutputFileAllowPng to false when OutputFileFormat == jpg.
        /// </summary>
        [Fact]
        public void CoreConfiguration_AfterLoad_SynchronizesJpeg()
        {
            var config = new CoreConfiguration
            {
                OutputFileFormat = OutputFormat.jpg,
                OutputFileAllowPng = true,
                OutputFileJpegQuality = 90
            };

            config.AfterLoad();

            Assert.False(config.OutputFileAllowPng);
            Assert.Equal(OutputFormat.jpg, config.OutputFileFormat);
            Assert.Equal(100, config.OutputFileJpegQuality);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsDirectoryWritable_NullOrWhitespace_ReturnsFalse(string? path)
        {
            Assert.False(EditorExportService.IsDirectoryWritable(path));
        }

        [Fact]
        public void IsDirectoryWritable_ValidDirectory_ReturnsTrue()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_WritableProbe_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                Assert.True(EditorExportService.IsDirectoryWritable(tempDir));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void IsDirectoryWritable_InvalidPathChars_ReturnsFalse()
        {
            Assert.False(EditorExportService.IsDirectoryWritable("Z:\\Invalid::Path??\\<Illegal>"));
        }

        [Fact]
        public async Task ResolveDownloadTargetAsync_PickerCancelled_FallsBackToTempFolderWithIsDownloadsFolderFalse()
        {
            var target = await EditorExportService.ResolveDownloadTargetAsync(
                userDownloadPath: "C:\\NonExistent_Invalid_Path_999",
                folderPicker: () => Task.FromResult<string?>(null),
                onUserPathConfigured: null,
                customDefaultPath: "C:\\NonExistent_Invalid_Path_888");

            Assert.Equal(Path.Combine(Path.GetTempPath(), "FreeSnip"), target.Path);
            Assert.False(target.IsDownloadsFolder);
        }

        [Fact]
        public async Task ResolveDownloadTargetAsync_PickerSelectsWritableFolder_PersistsAndReturnsTrue()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip_PickedFolder_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string? persistedPath = null;
                var target = await EditorExportService.ResolveDownloadTargetAsync(
                    userDownloadPath: "C:\\NonExistent_Invalid_Path_111",
                    folderPicker: () => Task.FromResult<string?>(tempDir),
                    onUserPathConfigured: p => persistedPath = p,
                    customDefaultPath: "C:\\NonExistent_Invalid_Path_222");

                Assert.Equal(tempDir, target.Path);
                Assert.True(target.IsDownloadsFolder);
                Assert.Equal(tempDir, persistedPath);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}

