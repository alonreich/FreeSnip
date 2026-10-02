using System;
using System.IO;
using System.Runtime.InteropServices;

namespace freesnip.foundation.core
{
    /// <summary>
    /// Resolves Windows known folders through the shell rather than guessing them from the
    /// user profile path. Downloads in particular is routinely redirected (OneDrive, a second
    /// drive, a network share); assuming %USERPROFILE%\Downloads makes FreeSnip believe the
    /// folder is missing and prompt the user for one it already has.
    /// </summary>
    public static class KnownFolders
    {
        private static readonly Guid FolderIdDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
        private static extern string SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags,
            IntPtr hToken);

        /// <summary>The user's real Downloads folder, or null when it cannot be resolved.</summary>
        public static string GetDownloadsPath()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string path = SHGetKnownFolderPath(FolderIdDownloads, 0u, IntPtr.Zero);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(KnownFolders)).Warn("SHGetKnownFolderPath(Downloads) failed; falling back to the profile path.", ex);
            }

            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile)) return Path.Combine(profile, "Downloads");
            }
            catch
            {
            }

            return null;
        }
    }
}
