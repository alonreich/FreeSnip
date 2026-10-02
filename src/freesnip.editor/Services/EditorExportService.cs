#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using freesnip.foundation.core;

namespace freesnip.editor.Services
{
    public readonly struct DownloadTarget
    {
        public DownloadTarget(string path, bool isDownloadsFolder)
        {
            Path = path;
            IsDownloadsFolder = isDownloadsFolder;
        }

        public string Path { get; }

        /// <summary>False when fallen back to the temp folder, so the notification overlay can distinguish the destination.</summary>
        public bool IsDownloadsFolder { get; }
    }

    public static class EditorExportService
    {
        private static readonly log4net.ILog Log = LogHelper.GetLogger(typeof(EditorExportService));

        public static string? SanitizeFileName(string? name, int maxLength = 40)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string sanitized = sb.ToString().Trim('_');
            if (sanitized.Length > maxLength)
            {
                sanitized = sanitized.Substring(0, maxLength).Trim('_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
        }

        public static string GenerateDownloadFileName(string? sourceTitle, bool allowPng = true, DateTime? timestamp = null)
        {
            string? safeTitle = SanitizeFileName(sourceTitle);
            string prefix = string.IsNullOrWhiteSpace(safeTitle) ? "Capture" : safeTitle;
            string ext = allowPng ? "png" : "jpg";
            DateTime time = timestamp ?? DateTime.Now;
            return $"{prefix}_{time:yyyy-MM-dd_HH-mm-ss_fff}.{ext}";
        }

        public static string GenerateClipboardBackupFileName(bool allowPng = true, DateTime? timestamp = null)
        {
            string ext = allowPng ? "png" : "jpg";
            DateTime time = timestamp ?? DateTime.Now;
            return $"Capture_{time:yyyy-MM-dd HH_mm_ss_fff}.{ext}";
        }

        public static Task SaveImageAsync(Image img, string path, bool allowPng = true, int jpegQuality = 100)
        {
            return Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Log.Info($"[STEP:START] SaveImageAsync - Destination: '{path}', AllowPng: {allowPng}, Quality: {jpegQuality}, Dimensions: ({img.Width}x{img.Height})");
                try
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (allowPng)
                    {
                        img.Save(path, new PngEncoder());
                    }
                    else
                    {
                        img.Save(path, new JpegEncoder { Quality = Math.Clamp(jpegQuality, 1, 100) });
                    }
                    long bytes = 0;
                    try { bytes = new FileInfo(path).Length; } catch { }
                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] SaveImageAsync ({sw.ElapsedMilliseconds}ms) - Written {bytes} bytes to '{path}'.");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Log.Error($"[STEP:FAIL] SaveImageAsync ({sw.ElapsedMilliseconds}ms) - Failed saving image to '{path}'", ex);
                    throw;
                }
            });
        }

        public static byte[] EncodeImage(Image img, bool allowPng = true, int jpegQuality = 100)
        {
            using var ms = new MemoryStream();
            if (allowPng)
            {
                img.Save(ms, new PngEncoder());
            }
            else
            {
                img.Save(ms, new JpegEncoder { Quality = Math.Clamp(jpegQuality, 1, 100) });
            }
            return ms.ToArray();
        }

        public static void ApplyFrameBorder(Image img, int thickness, SixLabors.ImageSharp.Color borderColor)
        {
            if (img == null || thickness <= 0) return;
            int w = img.Width;
            int h = img.Height;
            img.Mutate(x => x.Pad(w + 2 * thickness, h + 2 * thickness, borderColor));
        }

        public static void ApplyFrameBorder(Image img, int thickness, string? hexColor = null)
        {
            if (img == null || thickness <= 0) return;
            var borderColor = SixLabors.ImageSharp.Color.FromRgb(0x43, 0x43, 0x43);
            if (!string.IsNullOrWhiteSpace(hexColor))
            {
                try
                {
                    string hex = hexColor.Trim();
                    if (!hex.StartsWith("#")) hex = "#" + hex;
                    borderColor = SixLabors.ImageSharp.Color.ParseHex(hex);
                }
                catch { }
            }
            ApplyFrameBorder(img, thickness, borderColor);
        }

        public static async Task<bool> SaveToHistoryBackupAsync(
            string fileName,
            Image img,
            bool keepBackup,
            bool allowPng = true,
            int jpegQuality = 100,
            string? customBackupDir = null)
        {
            if (!keepBackup) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"[STEP:START] SaveToHistoryBackupAsync - File: '{fileName}'");

            try
            {
                string tempDir = customBackupDir ?? Path.Combine(Path.GetTempPath(), "FreeSnip");
                Directory.CreateDirectory(tempDir);
                string backupPath = Path.Combine(tempDir, fileName);
                await SaveImageAsync(img, backupPath, allowPng, jpegQuality).ConfigureAwait(false);
                sw.Stop();
                Log.Info($"[STEP:SUCCESS] SaveToHistoryBackupAsync ({sw.ElapsedMilliseconds}ms) - Backup created at '{backupPath}'.");
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log.Error($"[STEP:FAIL] SaveToHistoryBackupAsync ({sw.ElapsedMilliseconds}ms) - [BACKUP_FAILURE] Could not write to temp folder.", ex);
                return false;
            }
        }

        public static bool IsDirectoryWritable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!Directory.Exists(path))
                {
                    return false;
                }

                string probeFile = Path.Combine(path, $".freesnip_probe_{Guid.NewGuid():N}.tmp");
                using (var fs = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
                {
                    fs.WriteByte(0);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[DIR_PROBE_FAILED] Directory is not writable: {path}. Error: {ex.Message}");
                return false;
            }
        }

        public static async Task<DownloadTarget> ResolveDownloadTargetAsync(
            string? userDownloadPath,
            Func<Task<string?>>? folderPicker = null,
            Action<string>? onUserPathConfigured = null,
            string? customDefaultPath = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"[STEP:START] ResolveDownloadTargetAsync - UserPath: '{userDownloadPath}', CustomDefault: '{customDefaultPath}'");

            if (!string.IsNullOrEmpty(userDownloadPath) && IsDirectoryWritable(userDownloadPath))
            {
                sw.Stop();
                Log.Info($"[STEP:SUCCESS] ResolveDownloadTargetAsync ({sw.ElapsedMilliseconds}ms) - Selected user download path: '{userDownloadPath}'.");
                return new DownloadTarget(userDownloadPath, true);
            }

            string defaultPath = customDefaultPath ?? KnownFolders.GetDownloadsPath();
            if (!string.IsNullOrEmpty(defaultPath) && IsDirectoryWritable(defaultPath))
            {
                sw.Stop();
                Log.Info($"[STEP:SUCCESS] ResolveDownloadTargetAsync ({sw.ElapsedMilliseconds}ms) - Selected system downloads folder: '{defaultPath}'.");
                return new DownloadTarget(defaultPath, true);
            }

            if (folderPicker != null)
            {
                Log.Info("[STEP:INFO] ResolveDownloadTargetAsync - Prompting user folder picker.");
                string? selected = await folderPicker().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(selected) && IsDirectoryWritable(selected))
                {
                    onUserPathConfigured?.Invoke(selected);
                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] ResolveDownloadTargetAsync ({sw.ElapsedMilliseconds}ms) - Folder picked: '{selected}'.");
                    return new DownloadTarget(selected, true);
                }
            }

            sw.Stop();
            Log.Warn($"[STEP:WARN] ResolveDownloadTargetAsync ({sw.ElapsedMilliseconds}ms) - [DOWNLOAD_FALLBACK] Downloads folder unavailable and no folder was chosen; saving to FreeSnip temp folder.");
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
            Directory.CreateDirectory(tempDir);
            return new DownloadTarget(tempDir, false);
        }
    }
}
