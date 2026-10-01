using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace freesnip.forms
{
    public enum ScrollHudState
    {
        Initial,
        Active,
        Idle,
        LimitReached
    }

    public partial class ScrollCaptureBarWindow : Window
    {
        private TextBlock _screensBadge;
        private TextBlock _subtextHint;
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
            _screensBadge = this.FindControl<TextBlock>("ScreensBadge");
            _subtextHint = this.FindControl<TextBlock>("SubtextHint");
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            var hwnd = this.TryGetPlatformHandle()?.Handle;
            if (hwnd != null && hwnd.Value != IntPtr.Zero)
            {
                long exStyle = GetWindowLongPtr(hwnd.Value, GWL_EXSTYLE).ToInt64();
                exStyle |= WS_EX_NOACTIVATE;
                SetWindowLongPtr(hwnd.Value, GWL_EXSTYLE, new IntPtr(exStyle));
            }
        }

        public void UpdateHudState(ScrollHudState state, double screens = 1.0, int heightPx = 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_screensBadge == null || _subtextHint == null) return;
                switch (state)
                {
                    case ScrollHudState.Initial:
                        _screensBadge.Text = "Recording started. Scroll down now using your mouse wheel.";
                        _subtextHint.Text = "Space / Enter finishes · Esc cancels";
                        break;
                    case ScrollHudState.Active:
                        _screensBadge.Text = $"Scrolling detected: {screens:0.0} screens captured ({heightPx} px)";
                        _subtextHint.Text = "Space / Enter finishes · Esc cancels";
                        break;
                    case ScrollHudState.Idle:
                        _screensBadge.Text = "Waiting for scroll input...";
                        _subtextHint.Text = $"{screens:0.0} screens captured ({heightPx} px) · Finish to save";
                        break;
                    case ScrollHudState.LimitReached:
                        _screensBadge.Text = "Memory limit reached (180 MP).";
                        _subtextHint.Text = "Click Finish & Open Editor or press Space/Enter to save.";
                        break;
                }
            });
        }

        public void UpdateStats(double screens, int frames)
        {
            UpdateHudState(frames > 1 ? ScrollHudState.Active : ScrollHudState.Initial, screens, 0);
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
