using Avalonia;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace freesnip.editor.forms
{
    public partial class NotificationOverlayWindow : Window
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private static int _activeToasts = 0;
        private static readonly object _toastLock = new object();

        public NotificationOverlayWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
        }

        public static int GetOverlayDurationMs()
        {
            int blinkTotalMs = 1000;
            try
            {
                var blinkConfig = freesnip.foundation.IniFile.IniConfig.GetIniSection<freesnip.foundation.core.CoreConfiguration>();
                if (blinkConfig != null) blinkTotalMs = Math.Clamp(blinkConfig.NotificationOverlayDurationMs, 250, 10000);
            }
            catch { }
            return blinkTotalMs;
        }

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        private static Window ResolveContextWindow(Window owner)
        {
            if (owner != null) return owner;
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            return desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.Windows.FirstOrDefault();
        }

        /// <summary>
        /// Picks the screen to show an overlay on. Falls back to the screen under the mouse when
        /// SnapVox has no window open - which is exactly the case after a painter-mode OCR, where
        /// the old code silently dropped the confirmation instead of showing it.
        /// </summary>
        private static Screen ResolveTargetScreen(Window contextWindow, out bool anchored)
        {
            anchored = false;

            if (contextWindow != null)
            {
                try
                {
                    var fromWindow = contextWindow.Screens.ScreenFromWindow(contextWindow) ?? contextWindow.Screens.Primary;
                    if (fromWindow != null)
                    {
                        anchored = true;
                        return fromWindow;
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch
                {
                }
            }

            try
            {
                var probe = new Window();
                try
                {
                    Screen screen = null;
                    if (GetCursorPos(out POINT cursor))
                    {
                        screen = probe.Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y));
                    }

                    return screen ?? probe.Screens.Primary;
                }
                finally
                {
                    probe.Close();
                }
            }
            catch (Exception ex)
            {
                freesnip.foundation.core.LogHelper.GetLogger(typeof(NotificationOverlayWindow)).Error("Could not resolve a screen for the notification overlay", ex);
                return null;
            }
        }

        public static void ShowNotification(string message, Window owner)
        {
            var contextWindow = ResolveContextWindow(owner);

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    Screen targetScreen = ResolveTargetScreen(contextWindow, out _);
                    if (targetScreen == null) return;

                    var window = new NotificationOverlayWindow();
                    var textBlock = window.FindControl<TextBlock>("NotificationText");
                    var icon = window.FindControl<TextBlock>("NotificationIcon");
                    var chrome = window.FindControl<Border>("NotificationChrome");
                    
                    var work = targetScreen.WorkingArea;
                    
                    if (textBlock != null) 
                    {
                        textBlock.Text = message;
                    }

                    var whiteBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
                    var vividCoralBrush = new SolidColorBrush(Color.Parse("#FF3355"));
                    var vividCyanBrush = new SolidColorBrush(Color.Parse("#00D2FF"));
                    var vividAmberBrush = new SolidColorBrush(Color.Parse("#FFB703"));
                    IBrush[] colors = { whiteBrush, vividCoralBrush, vividCyanBrush, vividAmberBrush };

                    if (textBlock != null) textBlock.Foreground = colors[0];
                    if (icon != null) icon.Foreground = colors[0];
                    if (chrome != null) chrome.BorderBrush = colors[0];

                    window.Show();
                    window.UpdateLayout();

                    double scaling = targetScreen.Scaling;
                    double dipWidth = window.Bounds.Width > 0 ? window.Bounds.Width : 420;
                    double dipHeight = window.Bounds.Height > 0 ? window.Bounds.Height : 100;
                    double physWidth = dipWidth * scaling;
                    double physHeight = dipHeight * scaling;

                    int posX = (int)Math.Round(work.X + (work.Width - physWidth) / 2.0);
                    int posY = (int)Math.Round(work.Y + (work.Height - physHeight) / 2.0);

                    window.Position = new PixelPoint(posX, posY);

                    try
                    {
                        int blinkTotalMs = GetOverlayDurationMs();
                        int blinkStepDelay = Math.Max(60, blinkTotalMs / 4);
                        for (int i = 0; i < 4; i++)
                        {
                            IBrush foreground = colors[i % colors.Length];
                            if (textBlock != null) textBlock.Foreground = foreground;
                            if (icon != null) icon.Foreground = foreground;
                            if (chrome != null) chrome.BorderBrush = foreground;
                            await Task.Delay(blinkStepDelay);
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                catch (Exception ex)
                {
                    freesnip.foundation.core.LogHelper.GetLogger(typeof(NotificationOverlayWindow)).Error("ShowNotification failed", ex);
                }
            });
        }

        public static void ShowLightToast(string message, Window owner)
        {
            var contextWindow = ResolveContextWindow(owner);

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    Screen targetScreen = ResolveTargetScreen(contextWindow, out bool anchoredToWindow);
                    if (targetScreen == null) return;
                    var work = targetScreen.WorkingArea;

                    var ownerBounds = anchoredToWindow
                        ? contextWindow.Bounds
                        : new Rect(0, 0, work.Width, work.Height);
                    var ownerPos = anchoredToWindow
                        ? contextWindow.Position
                        : new PixelPoint(work.X, work.Y);

                    int offset;
                    bool counted;
                    lock (_toastLock) { offset = _activeToasts++; counted = true; }

                    NotificationOverlayWindow window = null;
                    try
                    {
                    window = new NotificationOverlayWindow();
                    var chrome = window.FindControl<Border>("NotificationChrome");
                    var textBlock = window.FindControl<TextBlock>("NotificationText");
                    var icon = window.FindControl<TextBlock>("NotificationIcon");
                    var viewbox = window.FindControl<Viewbox>("NotificationViewbox");
                    if (viewbox != null) viewbox.Stretch = Avalonia.Media.Stretch.None;
                    
                    Application.Current.TryFindResource("SnapVoxPanelDarkBrush", out var bgResource);
                    Application.Current.TryFindResource("SnapVoxAccentBrush", out var fgResource);
                    var bgBrush = (bgResource as IBrush) ?? new SolidColorBrush(Color.FromArgb(204, 45, 45, 48));
                    var fgBrush = (fgResource as IBrush) ?? Brushes.Gold;

                    if (chrome != null)
                    {
                        chrome.Background = bgBrush;
                        chrome.Padding = new Thickness(14, 8);
                        chrome.MinWidth = 180;
                    }
                    if (textBlock != null) { 
                        textBlock.Text = message; 
                        textBlock.FontSize = 14;
                        textBlock.FontWeight = FontWeight.SemiBold;
                        textBlock.Foreground = fgBrush; 
                        textBlock.MaxWidth = 300;
                    }
                    if (icon != null) { 
                        icon.Text = "\uE946";
                        icon.FontSize = 16;
                        icon.Foreground = fgBrush;
                    }

                    window.Opacity = 0;
                    window.Show();
                    window.UpdateLayout();

                    var bounds = window.Bounds;
                    double scaling = targetScreen.Scaling > 0 ? targetScreen.Scaling : 1.0;
                    double physOwnerWidth = anchoredToWindow ? (ownerBounds.Width * scaling) : work.Width;
                    double physOwnerHeight = anchoredToWindow ? (ownerBounds.Height * scaling) : work.Height;
                    double physToastWidth = (bounds.Width > 0 ? bounds.Width : 200) * scaling;
                    double physToastHeight = (bounds.Height > 0 ? bounds.Height : 40) * scaling;
                    double margin = 30 * scaling;
                    double topMargin = anchoredToWindow ? (92 * scaling) : margin;
                    double stackGap = 5 * scaling;

                    GetCursorPos(out POINT cursor);

                    double relX = cursor.X - ownerPos.X;
                    double relY = cursor.Y - ownerPos.Y;
                    
                    bool isRight = relX > physOwnerWidth / 2.0;
                    bool isTop = relY < physOwnerHeight / 2.0;

                    double targetX = isRight ? ownerPos.X + margin : ownerPos.X + physOwnerWidth - physToastWidth - margin;
                    double targetY = isTop ? ownerPos.Y + physOwnerHeight - physToastHeight - margin : ownerPos.Y + topMargin;

                    targetY += isTop ? -(offset * (physToastHeight + stackGap)) : (offset * (physToastHeight + stackGap));

                    int clampedX = (int)Math.Max(work.X, Math.Min(work.X + work.Width - physToastWidth, targetX));
                    int clampedY = (int)Math.Max(work.Y, Math.Min(work.Y + work.Height - physToastHeight, targetY));

                    window.Position = new PixelPoint(clampedX, clampedY);

                    for (int i = 0; i < 5; i++) { window.Opacity += 0.2; await Task.Delay(40); }
                    window.Opacity = 1.0;

                    await Task.Delay(800);

                    for (int i = 0; i < 5; i++) { window.Opacity -= 0.2; await Task.Delay(40); }
                    }
                    finally
                    {
                        window?.Close();
                        if (counted) lock (_toastLock) { _activeToasts--; if (_activeToasts < 0) _activeToasts = 0; }
                    }
                }
                catch (Exception ex)
                {
                    freesnip.foundation.core.LogHelper.GetLogger(typeof(NotificationOverlayWindow)).Error("ShowLightToast failed", ex);
                }
            });
        }

    }

}


