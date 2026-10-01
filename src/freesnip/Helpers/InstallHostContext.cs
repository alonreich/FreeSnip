using System;
using System.IO;
using freesnip.foundation.core;

namespace freesnip.helpers;

/// <summary>
/// Tracks installer host mode without referencing Avalonia (touching Avalonia loads Skia native DLLs).
/// </summary>
internal static class InstallHostContext
{
    public static void WriteEarlyTrace(string message)
    {
        try
        {
            string path = DeploymentFootprint.TempInstallationLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DeploymentFootprint.TempAppFolder);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [EARLY] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
