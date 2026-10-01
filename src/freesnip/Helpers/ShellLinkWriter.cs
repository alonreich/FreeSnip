using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.Interop;

namespace freesnip.helpers;

[SupportedOSPlatform("windows")]
internal static class ShellLinkWriter
{
    public static void Create(
        string shortcutPath,
        string targetPath,
        string workingDirectory = null,
        string iconLocation = null,
        string description = null,
        string arguments = null)
    {
        if (string.IsNullOrEmpty(shortcutPath)) throw new ArgumentException("shortcutPath must be provided.", nameof(shortcutPath));
        if (string.IsNullOrEmpty(targetPath)) throw new ArgumentException("targetPath must be provided.", nameof(targetPath));

        string directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        IShellLinkW link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();

            link.SetPath(targetPath);

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                link.SetWorkingDirectory(workingDirectory);
            }

            if (!string.IsNullOrEmpty(arguments))
            {
                link.SetArguments(arguments);
            }

            if (!string.IsNullOrEmpty(description))
            {
                link.SetDescription(description);
            }

            if (!string.IsNullOrEmpty(iconLocation))
            {
                string iconPath = iconLocation.Trim();
                int iconIndex = 0;
                int comma = iconPath.LastIndexOf(',');
                if (comma > 0 && int.TryParse(iconPath.Substring(comma + 1).Trim(), out int idx))
                {
                    iconPath = iconPath.Substring(0, comma).Trim();
                    iconIndex = idx;
                }
                iconPath = iconPath.Trim('\"');
                link.SetIconLocation(iconPath, iconIndex);
            }

            var file = (IPersistFile)link;
            file.Save(shortcutPath, true);

            if (!File.Exists(shortcutPath))
            {
                throw new FileNotFoundException("Shortcut file was not created.", shortcutPath);
            }
        }
        finally
        {
            if (link is IDisposable d)
            {
                d.Dispose();
            }
            else if (link != null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    public static Task CreateAsync(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        string description = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Create(shortcutPath, targetPath, workingDirectory, iconLocation, description, arguments: null);
        return Task.CompletedTask;
    }

    public static Task CreateAsync(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        string description,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Create(shortcutPath, targetPath, workingDirectory, iconLocation, description, arguments);
        return Task.CompletedTask;
    }

    public static (string targetPath, string workingDirectory, string arguments, string description, string iconLocation, int iconIndex) Read(string shortcutPath)
    {
        if (string.IsNullOrEmpty(shortcutPath)) throw new ArgumentException("shortcutPath must be provided.", nameof(shortcutPath));
        if (!File.Exists(shortcutPath)) throw new FileNotFoundException("Shortcut file not found.", shortcutPath);

        IShellLinkW link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            file.Load(shortcutPath, 0);

            string target = link.GetPath();
            string dir = link.GetWorkingDirectory();
            string args = link.GetArguments();
            string desc = link.GetDescription();
            var (iconPath, iconIdx) = link.GetIconLocation();

            return (target, dir, args, desc, iconPath, iconIdx);
        }
        finally
        {
            if (link is IDisposable d)
            {
                d.Dispose();
            }
            else if (link != null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }
}
