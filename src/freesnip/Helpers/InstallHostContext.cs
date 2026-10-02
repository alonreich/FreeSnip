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
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
            writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [EARLY] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
