using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace freesnip.forms
{
    public enum ScrollHudState
    {
        Initial,
        Active,
        Idle,
        Paused,
        LimitReached
    }

    public partial class ScrollCaptureBarWindow : Window
    {
        private TextBlock _statsCounterText;
        private TextBlock _speedWarningText;
        private TextBlock _subtextHint;
        private ProgressBar _stitchProgressBar;
        private Button _pauseButton;
        private Button _finishButton;
        private Button _cancelButton;
        private Avalonia.Controls.Shapes.Ellipse _recDot;
        private TextBlock _recLabel;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        public ScrollCaptureBarWindow()
        {
            InitializeComponent();
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
            _statsCounterText = this.FindControl<TextBlock>("StatsCounterText");
            _speedWarningText = this.FindControl<TextBlock>("SpeedWarningText");
            _subtextHint = this.FindControl<TextBlock>("SubtextHint");
            _stitchProgressBar = this.FindControl<ProgressBar>("StitchProgressBar");
            _pauseButton = this.FindControl<Button>("PauseButton");
            _finishButton = this.FindControl<Button>("FinishButton");
            _cancelButton = this.FindControl<Button>("CancelButton");
            _recDot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("RecDot");
            _recLabel = this.FindControl<TextBlock>("RecLabel");
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            var hwnd = this.TryGetPlatformHandle()?.Handle;
            if (hwnd != null && hwnd.Value != IntPtr.Zero)
            {
                long exStyle = GetWindowLongPtr(hwnd.Value, GWL_EXSTYLE).ToInt64();
                exStyle |= WS_EX_NOACTIVATE;
                SetWindowLongPtr(hwnd.Value, GWL_EXSTYLE, new IntPtr(exStyle));
                try { SetWindowDisplayAffinity(hwnd.Value, WDA_EXCLUDEFROMCAPTURE); } catch { }
            }
        }

        public void UpdateHudState(ScrollHudState state, double screens = 1.0, int heightPx = 0, int frames = 1, bool isTooFast = false, int direction = 0)
        {
            Action update = () =>
            {
                if (_statsCounterText == null || _subtextHint == null) return;
                switch (state)
                {
                    case ScrollHudState.Initial:
                        _statsCounterText.Text = "1.0 screens · 0 px · 1 frame";
                        _statsCounterText.Foreground = (IBrush)this.FindResource("FreeSnipPrimaryTextBrush") ?? Brushes.White;
                        if (_speedWarningText != null) _speedWarningText.IsVisible = false;
                        _subtextHint.Text = "Recording active. Scroll down using your mouse wheel. Enter finishes · Esc cancels";
                        SetPausedVisual(false);
                        break;
                    case ScrollHudState.Active:
                        _statsCounterText.Text = $"{screens:0.0} screens · {heightPx:N0} px · {frames} frames";
                        _statsCounterText.Foreground = (IBrush)this.FindResource("FreeSnipPrimaryTextBrush") ?? Brushes.White;
                        if (_speedWarningText != null)
                        {
                            if (isTooFast)
                            {
                                _speedWarningText.Text = "⚠️ Scrolling too fast — slow down!";
                                _speedWarningText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                                _speedWarningText.IsVisible = true;
                                _subtextHint.Text = "Slow down your mouse wheel for seamless frame stitching · Enter finishes · Esc cancels";
                            }
                            else if (direction < 0)
                            {
                                _speedWarningText.Text = "⬆ Scrolling up";
                                _speedWarningText.Foreground = new SolidColorBrush(Color.Parse("#64B5F6"));
                                _speedWarningText.IsVisible = true;
                                _subtextHint.Text = "Scroll down to capture following content · Enter finishes · Esc cancels";
                            }
                            else
                            {
                                _speedWarningText.Text = "↓ Scrolling down";
                                _speedWarningText.Foreground = (IBrush)this.FindResource("FreeSnipSuccessHoverBrush") ?? Brushes.Lime;
                                _speedWarningText.IsVisible = true;
                                _subtextHint.Text = "Smooth capture in progress · Enter finishes · Esc cancels";
                            }
                        }
                        SetPausedVisual(false);
                        break;
                    case ScrollHudState.Idle:
                        _statsCounterText.Text = $"{screens:0.0} screens · {heightPx:N0} px · {frames} frames";
                        _statsCounterText.Foreground = (IBrush)this.FindResource("FreeSnipPrimaryTextBrush") ?? Brushes.White;
                        if (_speedWarningText != null) _speedWarningText.IsVisible = false;
                        _subtextHint.Text = "Waiting for scroll input... · Enter finishes · Esc cancels";
                        SetPausedVisual(false);
                        break;
                    case ScrollHudState.Paused:
                        _statsCounterText.Text = $"{screens:0.0} screens · {heightPx:N0} px · {frames} frames (PAUSED)";
                        _statsCounterText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                        if (_speedWarningText != null)
                        {
                            _speedWarningText.Text = "⏸ PAUSED";
                            _speedWarningText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                            _speedWarningText.IsVisible = true;
                        }
                        _subtextHint.Text = "Capture paused · Click Resume, press Space, or Enter to finish";
                        SetPausedVisual(true);
                        break;
                    case ScrollHudState.LimitReached:
                        _statsCounterText.Text = $"{screens:0.0} screens · {heightPx:N0} px (MAX CAPACITY)";
                        _statsCounterText.Foreground = (IBrush)this.FindResource("FreeSnipDestructiveHoverBrush") ?? Brushes.Red;
                        if (_speedWarningText != null)
                        {
                            _speedWarningText.Text = "⚠️ 180 MP REACHED";
                            _speedWarningText.Foreground = (IBrush)this.FindResource("FreeSnipDestructiveHoverBrush") ?? Brushes.Red;
                            _speedWarningText.IsVisible = true;
                        }
                        _subtextHint.Text = "Maximum composite memory reached · Click Finish or press Enter to save";
                        SetPausedVisual(true);
                        break;
                }
            };

            if (Dispatcher.UIThread.CheckAccess())
            {
                update();
            }
            else
            {
                Dispatcher.UIThread.Post(update);
            }
        }

        public void UpdateStats(double screens, int frames)
        {
            UpdateHudState(frames > 1 ? ScrollHudState.Active : ScrollHudState.Initial, screens, 0, frames);
        }

        public void SetHint(string hint)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_subtextHint != null)
                {
                    _subtextHint.Text = hint;
                }
            });
        }

        public void SetPausedState(bool paused)
        {
            Dispatcher.UIThread.Post(() => SetPausedVisual(paused));
        }

        private void SetPausedVisual(bool paused)
        {
            if (_pauseButton != null)
            {
                _pauseButton.Content = paused ? "▶ Resume" : "⏸ Pause";
                ToolTip.SetTip(_pauseButton, paused ? "Resume scroll capture" : "Pause capture to scroll past ads or wait for pages to load");
            }
            if (_recDot != null)
            {
                if (paused)
                {
                    _recDot.Classes.Remove("pulse");
                    _recDot.Fill = new SolidColorBrush(Color.Parse("#FFA500"));
                }
                else
                {
                    if (!_recDot.Classes.Contains("pulse")) _recDot.Classes.Add("pulse");
                    _recDot.Fill = (IBrush)this.FindResource("FreeSnipDestructiveHoverBrush") ?? Brushes.Red;
                }
            }
            if (_recLabel != null)
            {
                _recLabel.Text = paused ? "PAUSED" : "REC";
                _recLabel.Foreground = paused ? new SolidColorBrush(Color.Parse("#FFA500")) : ((IBrush)this.FindResource("FreeSnipDestructiveHoverBrush") ?? Brushes.Red);
            }
        }

        public void ShowStitchingProgress(int pct)
        {
            Action update = () =>
            {
                if (_statsCounterText != null)
                {
                    _statsCounterText.Text = $"Stitching frames: {pct}%...";
                    _statsCounterText.Foreground = (IBrush)this.FindResource("FreeSnipPrimaryTextBrush") ?? Brushes.White;
                }
                if (_speedWarningText != null) _speedWarningText.IsVisible = false;
                if (_subtextHint != null)
                {
                    _subtextHint.Text = "Combining captured content into high-resolution image...";
                }
                if (_stitchProgressBar != null)
                {
                    _stitchProgressBar.IsVisible = true;
                    _stitchProgressBar.Value = pct;
                }
                if (_finishButton != null) _finishButton.IsEnabled = false;
                if (_pauseButton != null) _pauseButton.IsEnabled = false;
                if (_cancelButton != null) _cancelButton.IsEnabled = false;
            };

            if (Dispatcher.UIThread.CheckAccess()) update();
            else Dispatcher.UIThread.Post(update);
        }

        public void ShowNoScrollWarning()
        {
            Action update = () =>
            {
                if (_statsCounterText != null)
                {
                    _statsCounterText.Text = "⚠️ No scroll movement detected yet";
                    _statsCounterText.Foreground = new SolidColorBrush(Color.Parse("#FFA500"));
                }
                if (_speedWarningText != null) _speedWarningText.IsVisible = false;
                if (_subtextHint != null)
                {
                    _subtextHint.Text = "Scroll your mouse wheel down first, or click Cancel (Esc) to exit.";
                }
                if (_finishButton != null) _finishButton.IsEnabled = true;
                if (_pauseButton != null) _pauseButton.IsEnabled = true;
                if (_cancelButton != null) _cancelButton.IsEnabled = true;
            };

            if (Dispatcher.UIThread.CheckAccess()) update();
            else Dispatcher.UIThread.Post(update);
        }

        private void OnPauseClick(object sender, RoutedEventArgs e)
        {
            ScrollCaptureWindow.TogglePauseRecording();
        }

        private void OnFinishClick(object sender, RoutedEventArgs e)
        {
            _ = ScrollCaptureWindow.FinishRecordingFromBarAsync();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            _ = ScrollCaptureWindow.ExitModeFromBarAsync();
        }

        private void OnPillPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }
    }
}

