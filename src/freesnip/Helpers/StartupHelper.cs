using freesnip.native;
using freesnip.native.foundation;
using System;
using freesnip.foundation.core.AvaloniaShims;
using log4net;
using Microsoft.Win32;
using System.IO;
using freesnip.foundation.core;

namespace freesnip.helpers
{
    public static class StartupHelper
    {
        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(StartupHelper));
        private const string RunKey6432 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApplicationName = "FreeSnip";
        private static readonly string[] LegacyNames = { "FreeSnip", "freesnip" };

        private static string GetExecutablePath(string arguments = null, string executablePath = null)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(executablePath) ? RuntimePathHelper.ExecutablePath : executablePath;
            string quotedPath = "\"" + resolvedPath + "\"";
            if (string.IsNullOrWhiteSpace(arguments)) return quotedPath;
            return quotedPath + " " + arguments.Trim();
        }

        public static object GetRunAllValue()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RunKey, false))
            {
                object runValue = key?.GetValue(ApplicationName);
                if (runValue != null) return runValue;
            }
            if (IntPtr.Size != 8) return null;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RunKey6432, false))
            {
                return key?.GetValue(ApplicationName);
            }
        }

        public static object GetRunUserValue()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                object runValue = key?.GetValue(ApplicationName);
                if (runValue != null) return runValue;
            }
            if (IntPtr.Size != 8) return null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey6432, false))
            {
                return key?.GetValue(ApplicationName);
            }
        }

        public static bool HasRunAll()
        {
            try { return GetRunAllValue() != null; }
            catch (Exception e) { Log.Error("Error retrieving RunAllValue", e); return false; }
        }

        public static bool HasRunUser()
        {
            try { return GetRunUserValue() != null; }
            catch (Exception e) { Log.Error("Error retrieving RunUserValue", e); return false; }
        }

        public static void DeleteRunAll()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        foreach (string name in LegacyNames) try { key.DeleteValue(name, false); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Error("Error in deleteRunAll.", e); }
            if (IntPtr.Size != 8) return;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RunKey6432, true))
                {
                    if (key != null)
                    {
                        foreach (string name in LegacyNames) try { key.DeleteValue(name, false); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Error("Error in deleteRunAll x64.", e); }
        }

        public static void DeleteRunUser()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        foreach (string name in LegacyNames) try { key.DeleteValue(name, false); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Error("Error in deleteRunUser.", e); }
            if (IntPtr.Size != 8) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey6432, true))
                {
                    if (key != null)
                    {
                        foreach (string name in LegacyNames) try { key.DeleteValue(name, false); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Error("Error in deleteRunUser x64.", e); }
        }

        public static void SetRunUser(string arguments = null, string executablePath = null)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true)
                    ?? throw new IOException("The Windows startup registry key could not be opened.");
                string command = GetExecutablePath(arguments, executablePath);
                key.SetValue(ApplicationName, command);
                if (!string.Equals(key.GetValue(ApplicationName) as string, command, StringComparison.Ordinal))
                    throw new IOException("Windows startup registration could not be verified.");
            }
            catch (Exception e) { Log.Error("Error in setRunUser.", e); throw; }
        }

        public static void DeleteStartupFolderShortcut()
        {
            try
            {
                string lnkName = Path.GetFileNameWithoutExtension(RuntimePathHelper.ExecutablePath) + ".lnk";
                string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string startupShortcut = Path.Combine(startupPath, lnkName);
                if (File.Exists(startupShortcut)) File.Delete(startupShortcut);
                string startupAll = Environment.GetEnvironmentVariable("ALLUSERSPROFILE") + @"\Microsoft\Windows\Start Menu\Programs\Startup";
                string startupAllShortcut = Path.Combine(startupAll, lnkName);
                if (File.Exists(startupAllShortcut)) File.Delete(startupAllShortcut);
            }
            catch (Exception e) { Log.Error("Error deleting startup shortcut.", e); }
        }

    }
}
