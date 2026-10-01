using System;
using System.IO;
using System.Runtime.InteropServices;

namespace freesnip.helpers
{
    internal static class NativeDllSearchPath
    {
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
            try
            {
                SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs | LoadLibrarySearchUserDirs);
                AddDllDirectory(fullPath);
            }
            catch
            {
                SetDllDirectory(fullPath);
            }
        }
    }
}
