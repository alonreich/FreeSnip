using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace freesnip.helpers
{
    internal static class NativeDllSearchPath
    {
        private static readonly object Sync = new();
        private static readonly List<IntPtr> DirectoryCookies = new();
        private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
        private const uint LoadLibrarySearchUserDirs = 0x00000400;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        public static void Register(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            string fullPath = Path.GetFullPath(directory);
            if (SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs | LoadLibrarySearchUserDirs))
            {
                IntPtr cookie = AddDllDirectory(fullPath);
                if (cookie != IntPtr.Zero)
                {
                    lock (Sync) DirectoryCookies.Add(cookie);
                    return;
                }
            }

            if (!SetDllDirectory(fullPath))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the native dependency directory.");
            }
        }
    }
}
