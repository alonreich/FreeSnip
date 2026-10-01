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

            try
            {
                string tempDir = customBackupDir ?? Path.Combine(Path.GetTempPath(), "FreeSnip");
                Directory.CreateDirectory(tempDir);
                await SaveImageAsync(img, Path.Combine(tempDir, fileName), allowPng, jpegQuality).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[BACKUP_FAILURE] Could not write to temp folder.", ex);
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

                // Lightweight probe: create a temporary probe file, write to it, and delete it immediately.
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
            // 1. CoreConfiguration.UserDownloadPath (if configured and writable)
            if (!string.IsNullOrEmpty(userDownloadPath) && IsDirectoryWritable(userDownloadPath))
            {
                return new DownloadTarget(userDownloadPath, true);
            }

            // 2. KnownFolders.GetDownloadsPath() (standard Windows shell Downloads location)
            string defaultPath = customDefaultPath ?? KnownFolders.GetDownloadsPath();
            if (!string.IsNullOrEmpty(defaultPath) && IsDirectoryWritable(defaultPath))
            {
                return new DownloadTarget(defaultPath, true);
            }

            // 3. Fallback: Prompt user ONCE with native folder picker
            if (folderPicker != null)
            {
                string? selected = await folderPicker().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(selected) && IsDirectoryWritable(selected))
                {
                    onUserPathConfigured?.Invoke(selected);
                    return new DownloadTarget(selected, true);
                }
            }

            Log.Warn("[DOWNLOAD_FALLBACK] Downloads folder unavailable and no folder was chosen; saving to the SnapVox temp folder instead.");
            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
            Directory.CreateDirectory(tempDir);
            return new DownloadTarget(tempDir, false);
        }
    }
}
