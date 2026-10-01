#nullable enable
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using AvaloniaColor = Avalonia.Media.Color;

namespace freesnip.editor.Services
{
    public static class ContextToolbarTheme
    {
        public static readonly SolidColorBrush DarkBgToolbarBg = new(AvaloniaColor.Parse("#F8F9FA"));
        public static readonly SolidColorBrush DarkBgToolbarBorder = new(AvaloniaColor.Parse("#007ACC"));
        public static readonly SolidColorBrush DarkBgSep = new(AvaloniaColor.Parse("#D0D4DC"));
        public static readonly SolidColorBrush DarkBgDupBtnBg = new(AvaloniaColor.Parse("#E7F0FD"));
        public static readonly SolidColorBrush DarkBgDupBtnBorder = new(AvaloniaColor.Parse("#B6D4FE"));
        public static readonly SolidColorBrush DarkBgDupIcon = new(AvaloniaColor.Parse("#0D6EFD"));
        public static readonly SolidColorBrush DarkBgDelBtnBg = new(AvaloniaColor.Parse("#FFEAEA"));
        public static readonly SolidColorBrush DarkBgDelBtnBorder = new(AvaloniaColor.Parse("#FFC2C2"));
        public static readonly SolidColorBrush DarkBgDelIcon = new(AvaloniaColor.Parse("#DC3545"));
        public static readonly SolidColorBrush DarkBgAppBtnBg = new(AvaloniaColor.Parse("#E8F5E9"));
        public static readonly SolidColorBrush DarkBgAppBtnBorder = new(AvaloniaColor.Parse("#A5D6A7"));
        public static readonly SolidColorBrush DarkBgAppIcon = new(AvaloniaColor.Parse("#2E7D32"));

        public static readonly SolidColorBrush BrightBgToolbarBg = new(AvaloniaColor.Parse("#1A1C20"));
        public static readonly SolidColorBrush BrightBgToolbarBorder = new(AvaloniaColor.Parse("#00B4D8"));
        public static readonly SolidColorBrush BrightBgSep = new(AvaloniaColor.Parse("#3E4451"));
        public static readonly SolidColorBrush BrightBgDupBtnBg = new(AvaloniaColor.Parse("#262A33"));
        public static readonly SolidColorBrush BrightBgDupBtnBorder = new(AvaloniaColor.Parse("#3E4758"));
        public static readonly SolidColorBrush BrightBgDupIcon = new(AvaloniaColor.Parse("#60A5FA"));
        public static readonly SolidColorBrush BrightBgDelBtnBg = new(AvaloniaColor.Parse("#3D1418"));
        public static readonly SolidColorBrush BrightBgDelBtnBorder = new(AvaloniaColor.Parse("#6B2129"));
        public static readonly SolidColorBrush BrightBgDelIcon = new(AvaloniaColor.Parse("#FF6B6B"));
        public static readonly SolidColorBrush BrightBgAppBtnBg = new(AvaloniaColor.Parse("#14321F"));
        public static readonly SolidColorBrush BrightBgAppBtnBorder = new(AvaloniaColor.Parse("#1F5932"));
        public static readonly SolidColorBrush BrightBgAppIcon = new(AvaloniaColor.Parse("#4ADE80"));

        public static readonly SolidColorBrush MidBgToolbarBg = new(AvaloniaColor.Parse("#1E2126"));
        public static readonly SolidColorBrush MidBgToolbarBorder = new(AvaloniaColor.Parse("#3880FF"));
        public static readonly SolidColorBrush MidBgSep = new(AvaloniaColor.Parse("#444B59"));
        public static readonly SolidColorBrush MidBgDupBtnBg = new(AvaloniaColor.Parse("#323742"));
        public static readonly SolidColorBrush MidBgDupBtnBorder = new(AvaloniaColor.Parse("#4A5263"));
        public static readonly SolidColorBrush MidBgDupIcon = new(AvaloniaColor.Parse("#FFFFFF"));
        public static readonly SolidColorBrush MidBgDelBtnBg = new(AvaloniaColor.Parse("#8C1D28"));
        public static readonly SolidColorBrush MidBgDelBtnBorder = new(AvaloniaColor.Parse("#AB2E3B"));
        public static readonly SolidColorBrush MidBgDelIcon = new(AvaloniaColor.Parse("#FFFFFF"));
        public static readonly SolidColorBrush MidBgAppBtnBg = new(AvaloniaColor.Parse("#1A6B35"));
        public static readonly SolidColorBrush MidBgAppBtnBorder = new(AvaloniaColor.Parse("#288C48"));
        public static readonly SolidColorBrush MidBgAppIcon = new(AvaloniaColor.Parse("#FFFFFF"));

        public enum ThemeMode
        {
            DarkBackground,
            BrightBackground,
            MidtoneBackground
        }

        public static ThemeMode GetThemeMode(double luminance)
        {
            if (luminance < 0.38) return ThemeMode.DarkBackground;
            if (luminance > 0.65) return ThemeMode.BrightBackground;
            return ThemeMode.MidtoneBackground;
        }

        public static void ApplyTheme(
            Border? toolbar,
            double luminance,
            Button? dupBtn,
            Button? delBtn,
            Button? appBtn,
            Path? dupIcon,
            Path? delIcon,
            Path? appIcon,
            Rectangle? sep1,
            Rectangle? sep2)
        {
            if (toolbar == null) return;

            var mode = GetThemeMode(luminance);
            switch (mode)
            {
                case ThemeMode.DarkBackground:
                    toolbar.Background = DarkBgToolbarBg;
                    toolbar.BorderBrush = DarkBgToolbarBorder;
                    if (sep1 != null) sep1.Fill = DarkBgSep;
                    if (sep2 != null) sep2.Fill = DarkBgSep;

                    if (dupBtn != null)
                    {
                        dupBtn.Background = DarkBgDupBtnBg;
                        dupBtn.BorderBrush = DarkBgDupBtnBorder;
                    }
                    if (dupIcon != null) dupIcon.Fill = DarkBgDupIcon;

                    if (delBtn != null)
                    {
                        delBtn.Background = DarkBgDelBtnBg;
                        delBtn.BorderBrush = DarkBgDelBtnBorder;
                    }
                    if (delIcon != null) delIcon.Fill = DarkBgDelIcon;

                    if (appBtn != null)
                    {
                        appBtn.Background = DarkBgAppBtnBg;
                        appBtn.BorderBrush = DarkBgAppBtnBorder;
                    }
                    if (appIcon != null) appIcon.Fill = DarkBgAppIcon;
                    break;

                case ThemeMode.BrightBackground:
                    toolbar.Background = BrightBgToolbarBg;
                    toolbar.BorderBrush = BrightBgToolbarBorder;
                    if (sep1 != null) sep1.Fill = BrightBgSep;
                    if (sep2 != null) sep2.Fill = BrightBgSep;

                    if (dupBtn != null)
                    {
                        dupBtn.Background = BrightBgDupBtnBg;
                        dupBtn.BorderBrush = BrightBgDupBtnBorder;
                    }
                    if (dupIcon != null) dupIcon.Fill = BrightBgDupIcon;

                    if (delBtn != null)
                    {
                        delBtn.Background = BrightBgDelBtnBg;
                        delBtn.BorderBrush = BrightBgDelBtnBorder;
                    }
                    if (delIcon != null) delIcon.Fill = BrightBgDelIcon;

                    if (appBtn != null)
                    {
                        appBtn.Background = BrightBgAppBtnBg;
                        appBtn.BorderBrush = BrightBgAppBtnBorder;
                    }
                    if (appIcon != null) appIcon.Fill = BrightBgAppIcon;
                    break;

                case ThemeMode.MidtoneBackground:
                    toolbar.Background = MidBgToolbarBg;
                    toolbar.BorderBrush = MidBgToolbarBorder;
                    if (sep1 != null) sep1.Fill = MidBgSep;
                    if (sep2 != null) sep2.Fill = MidBgSep;

                    if (dupBtn != null)
                    {
                        dupBtn.Background = MidBgDupBtnBg;
                        dupBtn.BorderBrush = MidBgDupBtnBorder;
                    }
                    if (dupIcon != null) dupIcon.Fill = MidBgDupIcon;

                    if (delBtn != null)
                    {
                        delBtn.Background = MidBgDelBtnBg;
                        delBtn.BorderBrush = MidBgDelBtnBorder;
                    }
                    if (delIcon != null) delIcon.Fill = MidBgDelIcon;

                    if (appBtn != null)
                    {
                        appBtn.Background = MidBgAppBtnBg;
                        appBtn.BorderBrush = MidBgAppBtnBorder;
                    }
                    if (appIcon != null) appIcon.Fill = MidBgAppIcon;
                    break;
            }
        }
    }
}
