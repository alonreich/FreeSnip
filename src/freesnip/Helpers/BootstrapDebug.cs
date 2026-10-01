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
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
        }
    }
}
