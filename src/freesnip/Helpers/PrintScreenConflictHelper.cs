using System;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace freesnip.helpers
{
    /// <summary>
    /// Detects whether the Print Screen key is currently being taken by Windows itself
    /// (Snipping Tool / Snip &amp; Sketch - the Windows 10/11 "Use the Print screen key to open
    /// screen capture" setting) or by any other program, in which case FreeSnip hotkeys that
    /// rely on Print Screen cannot work. Used for the boot toast and the red Settings warning.
    /// </summary>
    public static class PrintScreenConflictHelper
    {
        /// <summary>
        /// Windows 10/11 setting "Use the Print screen key to open screen capture"
        /// (Settings &gt; Accessibility &gt; Keyboard). When enabled, Windows grabs Print Screen
        /// and opens the Snipping Tool, so FreeSnip cannot receive the key.
        /// </summary>
        public static bool IsWindowsSnippingPrintScreenEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
                if (key == null) return false;
                object raw = key.GetValue("PrintScreenKeyForSnippingEnabled");
                return raw switch
                {
                    int i => i == 1,
                    string s => int.TryParse(s, out var parsed) && parsed == 1,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True only when FreeSnip actually failed to register one of its configured Print Screen hotkeys.
        /// A successful RegisterHotKey is ground truth that FreeSnip owns the key, so the Windows snipping
        /// setting alone is never reported as a conflict; it only selects the explanation shown to the user.
        /// </summary>
        public static bool IsPrintScreenBlocked(out string reason)
        {
            if (!HotkeyManager.HasPrintScreenRegistrationFailure)
            {
                reason = null;
                return false;
            }

            reason = IsWindowsSnippingPrintScreenEnabled()
                ? "Windows is grabbing the Print Screen key. The Windows setting \"Use the Print screen key to open screen capture\" is turned ON, so the Windows Snipping Tool (Snip & Sketch) steals Print Screen before FreeSnip can see it."
                : "Another program is currently holding the Print Screen key, so FreeSnip could not register its Print Screen hotkeys.";
            return true;
        }

        /// <summary>Simple-English explanation shown inside the red banner in Settings.</summary>
        public static string BuildSettingsWarning(string reason)
        {
            return
                "FreeSnip CANNOT use the Print Screen key right now." + Environment.NewLine + Environment.NewLine +
                reason + Environment.NewLine + Environment.NewLine +
                "This means every hotkey below that uses Print Screen will NOT work." + Environment.NewLine + Environment.NewLine +
                "How to fix it (pick one):" + Environment.NewLine +
                "1. Open Windows Settings, go to Accessibility, then Keyboard, and turn OFF \"Use the Print screen key to open screen capture\"." + Environment.NewLine +
                "2. Or click a hotkey box below, press a different key (for example F9), then click \"Save and Close\".";
        }

        /// <summary>Popup notification in oversimplified English, shown when the conflict is detected.</summary>
        public static void ShowConflictToast()
        {
            ToastHelper.ShowToast(
                "FreeSnip: Print Screen is NOT working",
                "Windows (or another app) took the Print Screen key. Your screenshot shortcut will not work. " +
                "Easy fix: open FreeSnip Settings - the problem is explained there in red - or turn OFF " +
                "\"Use the Print screen key to open screen capture\" in Windows Settings.");
        }

        /// <summary>Waits (bounded) until HotkeyManager finished registering hotkeys for the current session.</summary>
        public static async Task WaitForHotkeyRegistrationAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !HotkeyManager.RegistrationCompleted)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Boot-time check. Called once after HotkeyManager.Start(). Real Print Screen conflicts are
        /// reported by HotkeyManager.RegisterAll() after a confirmation pass. When registration succeeded,
        /// FreeSnip owns the key regardless of the Windows snipping setting, so no toast is shown here;
        /// the setting is only logged for diagnostics.
        /// </summary>
        public static async Task NotifyOnBootAsync()
        {
            try
            {
                await WaitForHotkeyRegistrationAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                if (IsWindowsSnippingPrintScreenEnabled() && !HotkeyManager.HasPrintScreenRegistrationFailure)
                {
                    BootstrapDebug.Log("PrintScreenConflictHelper: Windows snipping Print Screen setting is ON, but FreeSnip registered its Print Screen hotkeys successfully. No conflict reported.");
                }
            }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"PrintScreenConflictHelper: boot check failed: {ex.Message}");
            }
        }
    }
}
