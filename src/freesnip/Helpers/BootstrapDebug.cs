using System;
using System.IO;

namespace freesnip.helpers
{
    public static class BootstrapDebug
    {
        private static readonly string LogPath = DeploymentFootprint.TempInstallationLogPath;

        public static void Log(string message)
        {
            try
            {
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
        }
    }
}
