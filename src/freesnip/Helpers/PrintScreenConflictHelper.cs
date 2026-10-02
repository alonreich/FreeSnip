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
        /// True when the Print Screen key is unavailable to FreeSnip, either because the Windows
        /// snipping setting is enabled or because hotkey registration for a Print Screen combo failed.
        /// </summary>
        public static bool IsPrintScreenBlocked(out string reason)
        {
            if (IsWindowsSnippingPrintScreenEnabled())
            {
                reason = "Windows is grabbing the Print Screen key. The Windows setting \"Use the Print screen key to open screen capture\" is turned ON, so the Windows Snipping Tool (Snip & Sketch) steals Print Screen before FreeSnip can see it.";
                return true;
            }

            if (HotkeyManager.HasPrintScreenRegistrationFailure)
            {
                reason = "Another program is currently holding the Print Screen key, so FreeSnip could not register its Print Screen hotkeys.";
                return true;
            }

            reason = null;
            return false;
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
        /// Boot-time check. Called once after HotkeyManager.Start(). When registration already failed
        /// for Print Screen, RegisterAll() showed the friendly toast; this covers the remaining case
        /// where the Windows snipping setting is ON but registration appeared to succeed (Windows
        /// still intercepts the key at a lower level).
        /// </summary>
        public static async Task NotifyOnBootAsync()
        {
            try
            {
                await WaitForHotkeyRegistrationAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                bool windowsSnippingEnabled = IsWindowsSnippingPrintScreenEnabled();
                bool registrationFailedForPrintScreen = HotkeyManager.HasPrintScreenRegistrationFailure;

                if (windowsSnippingEnabled && !registrationFailedForPrintScreen)
                {
                    ToastHelper.ShowToast(
                        "FreeSnip: Print Screen is NOT working",
                        "Windows took the Print Screen key. FreeSnip cannot use it for screenshots. " +
                        "Easy fix: open Windows Settings, go to Accessibility, then Keyboard, and turn OFF " +
                        "\"Use the Print screen key to open screen capture\". Or open FreeSnip Settings and pick a different key.");
                }
            }
            catch
            {
            }
        }
    }
}
