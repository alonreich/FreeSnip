using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace freesnip.helpers;

internal static class DeploymentFootprint
{
    public const string AppName = "FreeSnip";
    public const string ScheduledTaskName = "FreeSnip";
    public const string InstallerMutexName = @"Global\FreeSnip_Installer";
    public const string ProgId = "freesnip.editor.1";
    public const string OpenWithShellName = "Open with FreeSnip";
    public const string DisplayName = "FreeSnip";

    public const string UninstallKeyName = "FreeSnip";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;
    public const string AppRegistryKeyPath = @"SOFTWARE\FreeSnip";

    public static readonly string ProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppName);

    public static readonly string InstallFolder = StartupTaskHelper.InstallFolder;

    public static readonly string TempInstallationLogPath = Path.Combine(Path.GetTempPath(), "FreeSnip_Installation.log");
    public static readonly string InstallLogPath = TempInstallationLogPath;

    public static readonly string RoamingAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName);

    public static readonly string LocalAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);

    public static readonly string TempAppFolder = Path.Combine(Path.GetTempPath(), AppName);
    public static readonly string DeploymentTempRoot = Path.Combine(TempAppFolder, "Lifecycle");
    public static readonly string UpgradeBackupsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeSnipUpgradeBackups");

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".ico" };

    public static readonly string[] RunKeyRelativePaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
    };

    public static readonly string[] RunValueNames =
    {
        AppName,
        DisplayName,
        "freesnip",
        "FreeSnip"
    };

    public static readonly string[] MuiCacheRelativePaths =
    {
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Microsoft\Windows\ShellNoRoam\MUICache",
        @"Software\Microsoft\Windows\Shell\MuiCache"
    };

    public static readonly string[] ShortcutFileNames =
    {
        "FreeSnip.lnk",
        "freesnip.lnk",
        "Uninstall FreeSnip.lnk"
    };

    public static IEnumerable<string> GetShortcutSearchFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
    }

    public static (RegistryHive Hive, RegistryView View, string SubKeyPath) GetCanonicalUninstallRegistryTarget()
    {
        return (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallKeyPath);
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKeyPath)> GetUninstallRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, UninstallKeyPath);
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FreeSnip");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName);
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName);
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FreeSnip");
            }
        }
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string Path)> GetAppRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, AppRegistryKeyPath);
                yield return (hive, view, @"SOFTWARE\FreeSnip");
                yield return (hive, view, @"SOFTWARE\freesnip");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\FreeSnip");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\freesnip");
            }
        }
    }

    public static IEnumerable<string> GetUserArtifactPatterns()
    {
        return Array.Empty<string>();
    }

    public static IEnumerable<string> GetDirectoryPurgeTargets(bool includeInstallFolder)
    {
        var dirs = new List<string>();
        if (includeInstallFolder) dirs.Add(InstallFolder);
        dirs.Add(ProgramDataFolder);
        dirs.Add(RoamingAppDataFolder);
        dirs.Add(LocalAppDataFolder);
        dirs.Add(TempAppFolder);
        dirs.Add(DeploymentTempRoot);

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> GetVerificationTargets()
    {
        yield return InstallFolder;
        yield return ProgramDataFolder;
        yield return RoamingAppDataFolder;
        yield return LocalAppDataFolder;
    }

    public static IEnumerable<string> GetFullVerificationTargets()
    {
        foreach (string target in GetVerificationTargets()) yield return target;
        yield return TempAppFolder;
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKeyPath)> GetFileAssociationVerificationTargets()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\" + ProgId);
        foreach (string ext in ImageExtensions)
        {
            yield return (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\" + ext + @"\shell\" + OpenWithShellName);
        }
    }
}
