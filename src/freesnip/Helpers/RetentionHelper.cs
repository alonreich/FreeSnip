using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;

namespace freesnip.helpers
{
    public static class RetentionHelper
    {
        private static readonly string TempStorage = Path.Combine(Path.GetTempPath(), "FreeSnip");
        private static readonly string[] LegacyTempStorages = new[]
        {
            Path.Combine(Path.GetTempPath(), "SnapVox"),
            Path.Combine(Path.GetTempPath(), "snapvox")
        };
        private static readonly object SyncRoot = new object();
        private static Timer _cleanupTimer;
        private static int _cleanupRunning;

        public static void Start()
        {
            lock (SyncRoot)
            {
                if (_cleanupTimer != null)
                {
                    return;
                }

                _cleanupTimer = new Timer(_ => RunCleanup(), null, TimeSpan.Zero, TimeSpan.FromHours(4));
            }
        }

        public static void Stop()
        {
            lock (SyncRoot)
            {
                _cleanupTimer?.Dispose();
                _cleanupTimer = null;
            }
        }

        public static void RunCleanup()
        {
            if (Interlocked.Exchange(ref _cleanupRunning, 1) != 0)
            {
                return;
            }

            if (!Directory.Exists(TempStorage) && !LegacyTempStorages.Any(Directory.Exists))
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
                return;
            }

            try
            {
                var now = DateTime.Now;
                var targets = new List<string> { TempStorage };
                targets.AddRange(LegacyTempStorages);

                foreach (string storage in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(storage)) continue;
                    CleanStorageRoot(storage, now);
                }
            }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"Retention cleanup failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
            }
        }

        private static void CleanStorageRoot(string root, DateTime now)
        {
            try
            {
                bool IsProtectedPath(string path)
                {
                    try
                    {
                        string relative = Path.GetRelativePath(root, path);
                        string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            string topDir = parts[0];
                            if (topDir.Equals("Lifecycle", StringComparison.OrdinalIgnoreCase) ||
                                topDir.Equals("Runtime", StringComparison.OrdinalIgnoreCase) ||
                                topDir.Equals("tessdata", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                    catch { }
                    return false;
                }

                var allFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                foreach (var file in allFiles)
                {
                    if (IsProtectedPath(file)) continue;
                    try
                    {
                        var creationTime = File.GetCreationTime(file);
                        if ((now - creationTime).TotalHours >= 24)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }

                var allDirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length);
                foreach (var dir in allDirs)
                {
                    if (IsProtectedPath(dir)) continue;
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            var creationTime = Directory.GetCreationTime(dir);
                            if ((now - creationTime).TotalHours >= 24)
                            {
                                Directory.Delete(dir);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"Failed to clean storage root {root}: {ex.Message}");
            }
        }
    }
}
