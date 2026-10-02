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
            try
            {
                string exe = ExecutablePath;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    {
                        return info.FileVersion;
                    }
                }
            }
            catch { }

            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(RuntimePathHelper).Assembly;
            string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int plus = informationalVersion.IndexOf('+');
                return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
            }
            return assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    public static bool TryParseVersion(string raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        if (Version.TryParse(s, out var v))
        {
            version = v;
            return true;
        }

        var sb = new System.Text.StringBuilder();
        int dotCount = 0;
        foreach (char c in s)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
            }
            else if (c == '.' && sb.Length > 0 && dotCount < 3)
            {
                sb.Append(c);
                dotCount++;
            }
            else
            {
                break;
            }
        }
        string candidate = sb.ToString().TrimEnd('.');
        if (dotCount == 0 && candidate.Length > 0) candidate += ".0";
        return Version.TryParse(candidate, out version);
    }
}
