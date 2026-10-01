using System;
using Avalonia.Media;
using freesnip.foundation.IniFile;

namespace freesnip.foundation.core
{
    public static class UiLayoutDirection
    {
        private static readonly string[] RightToLeftPrefixes = { "he", "iw", "ar", "fa", "ur", "yi" };

        public static bool IsRightToLeft
        {
            get
            {
                try
                {
                    string language = IniConfig.GetIniSection<CoreConfiguration>()?.Language;
                    if (string.IsNullOrWhiteSpace(language)) return false;
                    foreach (string prefix in RightToLeftPrefixes)
                    {
                        if (language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch
                {
                }

                return false;
            }
        }

        public static FlowDirection Current => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public static void Apply(Avalonia.Controls.Control control)
        {
            if (control == null) return;
            try
            {
                control.FlowDirection = Current;
            }
            catch
            {
            }
        }
    }
}
