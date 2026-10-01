using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace freesnip.foundation.core;

public static class RuntimePathHelper
{
    public const string ProductName = "FreeSnip";

    public static string ExecutablePath
    {
        get
        {
            string path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path;

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 0 && Path.IsPathFullyQualified(args[0]))
            {
                return args[0];
            }

            try
            {
                path = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch
            {
            }

            if (args.Length > 0)
            {
                string fileName = Path.GetFileName(args[0]);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return Path.Combine(AppContext.BaseDirectory, fileName);
                }
            }

            return Path.Combine(AppContext.BaseDirectory, ProductName + ".exe");
        }
    }

    public static string StartupPath
    {
        get
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory)) return baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string directory = Path.GetDirectoryName(ExecutablePath);
            return string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
        }
    }

    public static string ProductVersion
    {
        get
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(RuntimePathHelper).Assembly;
            string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion)) return informationalVersion;
            return assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }
}
