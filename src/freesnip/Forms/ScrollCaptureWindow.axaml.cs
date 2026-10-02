using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using log4net;
using freesnip.foundation.core;
using freesnip.helpers;
using freesnip.native;
using freesnip.native.foundation;
using ImageSharpBgra = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32>;
using SixLabors.ImageSharp.Processing;
namespace freesnip.forms
{
    public partial class ScrollCaptureWindow : Window
    {
        private static readonly ILog Log = LogHelper.GetLogger(typeof(ScrollCaptureWindow));
        private static readonly Avalonia.Input.Cursor RecordingCursor = new Avalonia.Input.Cursor(StandardCursorType.Arrow);
        private static readonly object Sync = new object();
        private static readonly List<ScrollCaptureWindow> ActiveWindows = new List<ScrollCaptureWindow>();
        private static RECT SelectedRect = RECT.Empty;
        private static IntPtr SelectedWindowHandle = IntPtr.Zero;
        private static bool IsSelectedWindowElevated;
        private static ScrollCaptureRecorder Recorder;
        private static Window OwnerWindow;
        private static bool OwnerWasVisible;
        private static bool IsRecording;
        private static bool IsClosingAll;
        private static bool IsFinishing;
        private static ScrollCaptureBarWindow BarWindow;
        private static readonly bool IsFreeSnipElevated = Win32WindowHelper.IsProcessElevated((uint)Environment.ProcessId);


        private PixelRect _screenBounds;
        private Canvas _mainCanvas;
        private Border _highlightBorder;
        private Border _instructionBorder;
        private Ellipse _recordingDot;
        private TextBlock _instructionText;
        private TextBlock _statusText;
        private Button _exitButton;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        private static DispatcherTimer _pollTimer;
        private static int _lastAcceptedFrames = 0;
        private static int _lastCapturedHeight = 0;
        private static DateTime _lastMovementTime = DateTime.UtcNow;
        private static bool _hasScrolled = false;

        public void SetClickThrough(bool clickThrough)
        {
            var hwnd = this.TryGetPlatformHandle()?.Handle;
            if (hwnd == null || hwnd.Value == IntPtr.Zero) return;

            long exStyle = GetWindowLongPtr(hwnd.Value, GWL_EXSTYLE).ToInt64();
            if (clickThrough)
            {
                exStyle |= (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);
            }
            else
            {
                exStyle &= ~(WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);
            }

            SetWindowLongPtr(hwnd.Value, GWL_EXSTYLE, new IntPtr(exStyle));
            SetWindowPos(hwnd.Value, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private static void StartInputPolling()
        {
            if (_pollTimer != null) return;
            int ticks = 0;
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pollTimer.Tick += (s, e) =>
            {
                if (IsRecording)
                {
                    if (Recorder != null)
                    {
                        if (Recorder.IsSegmentCeilingReached || Recorder.IsPaused)
                        {
                            BroadcastStatus("LIMIT REACHED (PAUSED)", "Maximum memory reached (180 MP) · Space/Enter finishes");
                            BarWindow?.UpdateHudState(ScrollHudState.LimitReached);
                        }
                        else
                        {
                            int frames = Recorder.AcceptedFrames;
                            double screens = Recorder.EstimatedScreens;
                            int height = Recorder.TotalCapturedHeight;

                            if (frames > 1 && (frames > _lastAcceptedFrames || height > _lastCapturedHeight))
                            {
                                _hasScrolled = true;
                                _lastMovementTime = DateTime.UtcNow;
                                _lastAcceptedFrames = frames;
                                _lastCapturedHeight = height;
                            }

                            if (!_hasScrolled)
                            {
                                BroadcastStatus("SCROLLING ACTIVE", "Recording started. Scroll down now using your mouse wheel.");
                                BarWindow?.UpdateHudState(ScrollHudState.Initial);
                            }
                            else
                            {
                                bool isIdle = (DateTime.UtcNow - _lastMovementTime).TotalMilliseconds > 900;
                                if (isIdle)
                                {
                                    BroadcastStatus("WAITING FOR INPUT", $"Waiting for scroll input... | {screens:0.0} screens ({height} px)");
                                    BarWindow?.UpdateHudState(ScrollHudState.Idle, screens, height);
                                }
                                else
                                {
                                    BroadcastStatus("SCROLLING ACTIVE", $"Scrolling detected: {screens:0.0} screens captured ({height} px)");
                                    BarWindow?.UpdateHudState(ScrollHudState.Active, screens, height);
                                }
                            }
                        }
                    }
                }
                else
                {
                    StopInputPolling();
                    return;
                }
                
                ticks++;
                if (ticks < 10) return;

                bool delimiterPressed = IsConfiguredDelimiterPressed();
                bool enterPressed = (GetAsyncKeyState(0x0D) & 0x8000) != 0;

                if (delimiterPressed || enterPressed)
                {
                    StopInputPolling();
                    _ = FinishRecordingAsync();
                }
                else if ((GetAsyncKeyState(0x1B) & 0x8000) != 0)
                {
                    StopInputPolling();
                    _ = ExitModeAsync();
                }
            };
            _pollTimer.Start();
        }

        private static bool IsConfiguredDelimiterPressed()
        {
            var config = freesnip.foundation.IniFile.IniConfig.GetIniSection<CoreConfiguration>();
            string key = config.ScrollCaptureDelimiterHotkey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "Enter";
            }

            bool ctrlReq = key.Contains("Ctrl", StringComparison.OrdinalIgnoreCase);
            bool altReq = key.Contains("Alt", StringComparison.OrdinalIgnoreCase);
            bool shiftReq = key.Contains("Shift", StringComparison.OrdinalIgnoreCase);
            bool winReq = key.Contains("Win", StringComparison.OrdinalIgnoreCase);

            bool ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool winDown = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

            if (ctrlReq != ctrlDown) return false;
            if (altReq != altDown) return false;
            if (shiftReq != shiftDown) return false;
            if (winReq != winDown) return false;

            string keyName = key.Split('+').Last().Trim();
            int vkCode = 0x0D;
            if (string.Equals(keyName, "Enter", StringComparison.OrdinalIgnoreCase) || string.Equals(keyName, "Return", StringComparison.OrdinalIgnoreCase)) vkCode = 0x0D;
            else if (string.Equals(keyName, "Space", StringComparison.OrdinalIgnoreCase)) vkCode = 0x20;
            else if (string.Equals(keyName, "Escape", StringComparison.OrdinalIgnoreCase) || string.Equals(keyName, "Esc", StringComparison.OrdinalIgnoreCase)) vkCode = 0x1B;
            else if (string.Equals(keyName, "Tab", StringComparison.OrdinalIgnoreCase)) vkCode = 0x09;
            else if (Enum.TryParse<freesnip.foundation.core.AvaloniaShims.Keys>(keyName, true, out var parsedKey)) vkCode = (int)parsedKey;

            return (GetAsyncKeyState(vkCode) & 0x8000) != 0;
        }

        private static void StopInputPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer = null;
            }
        }


        public ScrollCaptureWindow()
        {
            InitializeComponent();
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
        }

        public ScrollCaptureWindow(PixelRect screenBounds)
        {
            _screenBounds = screenBounds;
            InitializeComponent();
            freesnip.foundation.core.UiLayoutDirection.Apply(this);

            double scaling = 1.0;
            try
            {
                var screen = Screens.ScreenFromPoint(new PixelPoint(screenBounds.X, screenBounds.Y));
                if (screen != null)
                {
                    scaling = screen.Scaling;
                }
            }
            catch
            {
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(screenBounds.X, screenBounds.Y);
            Width = screenBounds.Width / scaling;
            Height = screenBounds.Height / scaling;

            _mainCanvas = this.FindControl<Canvas>("MainCanvas");
            _highlightBorder = this.FindControl<Border>("HighlightBorder");
            _instructionBorder = this.FindControl<Border>("InstructionBorder");
            _recordingDot = this.FindControl<Ellipse>("RecordingDot");
            _instructionText = this.FindControl<TextBlock>("InstructionText");
            _statusText = this.FindControl<TextBlock>("StatusText");
            _exitButton = this.FindControl<Button>("ExitButton");

            KeyDown += OnKeyDown;
            Closed += OnClosed;

            LayoutFixedChrome();
            lock (Sync)
            {
                ActiveWindows.Add(this);
            }
        }

        public static async Task StartAsync(Window ownerWindow = null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ActiveWindows.Count > 0)
                {
                    for (int i = 0; i < ActiveWindows.Count; i++)
                    {
                        ActiveWindows[i].Activate();
                        ActiveWindows[i].Focus();
                    }
                    return;
                }

                OwnerWindow = ownerWindow;
                OwnerWasVisible = ownerWindow != null && ownerWindow.IsVisible;
                if (OwnerWasVisible)
                {
                    ownerWindow.Hide();
                }

                IsRecording = false;
                IsFinishing = false;
                IsClosingAll = false;
                SelectedRect = RECT.Empty;

                IReadOnlyList<PixelRect> screens = GetScreens(ownerWindow);
                if (screens == null || screens.Count == 0)
                {
                    RestoreOwner();
                    return;
                }

                try
                {
                    foreach (PixelRect screen in screens)
                    {
                        var window = new ScrollCaptureWindow(screen);
                        window.Show();
                        window.Activate();
                        window.Focus();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to initialize scroll capture overlay windows.", ex);
                    CloseAllOverlays();
                    RestoreOwner();
                }
            });
        }

        private static IReadOnlyList<PixelRect> GetScreens(Window owner)
        {
            try
            {
                var screens = owner?.Screens.All;
                if (screens != null && screens.Count > 0)
                {
                    return screens.Select(screen => screen.Bounds).ToList();
                }

                var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                screens = lifetime?.Windows.FirstOrDefault()?.Screens.All;
                if (screens != null && screens.Count > 0)
                {
                    return screens.Select(screen => screen.Bounds).ToList();
                }

                var probe = new Window();
                return probe.Screens.All.Select(screen => screen.Bounds).ToList();
            }
            catch
            {
                var probe = new Window();
                return probe.Screens.Primary == null ? Array.Empty<PixelRect>() : new[] { probe.Screens.Primary.Bounds };
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            Activate();
            Focus();
            UpdateSelectionFromCursor();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            lock (Sync)
            {
                ActiveWindows.Remove(this);
                if (!IsClosingAll && ActiveWindows.Count == 0)
                {
                    _ = ExitModeAsync();
                }
            }
        }

        private void OnPointerMoved(object sender, PointerEventArgs e)
        {
            if (!IsRecording)
            {
                UpdateSelectionFromCursor();
            }
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                if (!IsRecording)
                {
                    _ = ExitModeAsync();
                }
                e.Handled = true;
            }
            else if (props.IsLeftButtonPressed)
            {
                if (!IsRecording)
                {
                    StartRecording();
                }
                e.Handled = true;
            }
        }

        private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            if (IsRecording)
            {
                int delta = (int)(e.Delta.Y * 120);
                if (delta != 0)
                {
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                }
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _ = ExitModeAsync();
                e.Handled = true;
                return;
            }

            if (IsDelimiterKey(e))
            {
                if (IsRecording)
                {
                    _ = FinishRecordingAsync();
                }
                else
                {
                    StartRecording();
                }

                e.Handled = true;
            }
        }

        private static bool IsDelimiterKey(KeyEventArgs e)
        {
            var config = freesnip.foundation.IniFile.IniConfig.GetIniSection<CoreConfiguration>();
            string key = config.ScrollCaptureDelimiterHotkey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "Enter";
            }

            bool ctrl = key.Contains("Ctrl", StringComparison.OrdinalIgnoreCase);
            bool alt = key.Contains("Alt", StringComparison.OrdinalIgnoreCase);
            bool shift = key.Contains("Shift", StringComparison.OrdinalIgnoreCase);
            bool win = key.Contains("Win", StringComparison.OrdinalIgnoreCase);
            if (ctrl != e.KeyModifiers.HasFlag(KeyModifiers.Control)) return false;
            if (alt != e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
            if (shift != e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return false;
            if (win != e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return false;

            string keyName = key.Split('+').Last().Trim();
            return string.Equals(e.Key.ToString(), keyName, StringComparison.OrdinalIgnoreCase);
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            _ = ExitModeAsync();
        }

        private async void StartRecording()
        {
            RECT rect = SelectedRect.Normalize();
            if (rect.IsEmpty || rect.Width <= 20 || rect.Height <= 20)
            {
                BroadcastStatus("Point at a window", "No window selected");
                return;
            }

            if (IsSelectedWindowElevated && !IsFreeSnipElevated)
            {
                BroadcastStatus("ACCESS DENIED", "Run FreeSnip as Admin to capture");
                return;
            }

            try
            {
                if (SelectedWindowHandle != IntPtr.Zero)
                {
                    Win32WindowHelper.SetForegroundWindow(SelectedWindowHandle);
                    var config = freesnip.foundation.IniFile.IniConfig.GetIniSection<CoreConfiguration>();
                    int delay = Math.Max(150, config.CaptureDelay);
                    await Task.Delay(delay);
                }

                _lastAcceptedFrames = 0;
                _lastCapturedHeight = 0;
                _lastMovementTime = DateTime.UtcNow;
                _hasScrolled = false;

                foreach (var win in ActiveWindows) 
                { 
                    win.Cursor = RecordingCursor; 
                    win.SetClickThrough(true);
                    win.IsHitTestVisible = false;
                    win.Hide();
                }

                if (Dispatcher.UIThread.CheckAccess())
                {
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                }

                if (SelectedWindowHandle != IntPtr.Zero)
                {
                    Win32WindowHelper.SetForegroundWindow(SelectedWindowHandle);
                }

                await Task.Delay(100);

                Recorder = new ScrollCaptureRecorder(rect);
                Recorder.SegmentCeilingReached += () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        BroadcastStatus("LIMIT REACHED (PAUSED)", "Maximum memory reached (180 MP) · Enter finishes");
                        BarWindow?.UpdateHudState(ScrollHudState.LimitReached);
                    });
                };
                Recorder.Start();
                IsRecording = true;

                ShowFloatingBar(rect);
                StartInputPolling();
                BroadcastStatus("SCROLLING ACTIVE", "Recording started. Scroll down now using your mouse wheel.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not start scroll capture.", ex);
                foreach (var win in ActiveWindows)
                {
                    if (win._exitButton != null) win._exitButton.IsVisible = true;
                }
                if (!IsFreeSnipElevated)
                {
                    BroadcastStatus("ACCESS DENIED / BLOCKED", "Run FreeSnip as Admin to capture this app");
                }
                else
                {
                    BroadcastStatus("Try again", "Could not start recording");
                }
                IsRecording = false;
                Recorder = null;
            }
        }

        public static async Task FinishRecordingFromBarAsync()
        {
            StopInputPolling();
            await FinishRecordingAsync().ConfigureAwait(false);
        }

        public static async Task ExitModeFromBarAsync()
        {
            StopInputPolling();
            await ExitModeAsync().ConfigureAwait(false);
        }

        private static void ShowFloatingBar(RECT targetRect)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (BarWindow != null)
                    {
                        BarWindow.Close();
                        BarWindow = null;
                    }

                    BarWindow = new ScrollCaptureBarWindow();

                    double scaling = 1.0;
                    var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                    var probeWindow = lifetime?.Windows.FirstOrDefault();
                    var screens = probeWindow?.Screens;
                    if (screens != null)
                    {
                        var screen = screens.ScreenFromPoint(new PixelPoint(targetRect.Left + targetRect.Width / 2, targetRect.Top + 20))
                                     ?? screens.Primary;
                        if (screen != null)
                        {
                            scaling = screen.Scaling;
                            double barWidth = 580;
                            double targetCenterX = (targetRect.Left + targetRect.Width / 2.0);
                            double screenLeft = screen.Bounds.X;
                            double screenWidth = screen.Bounds.Width;

                            double desiredPixelX = Math.Clamp(targetCenterX - (barWidth * scaling / 2.0), screenLeft + 20, screenLeft + screenWidth - (barWidth * scaling) - 20);
                            double desiredPixelY = Math.Max(screen.Bounds.Y + 24, targetRect.Top + 16);

                            BarWindow.Position = new PixelPoint((int)desiredPixelX, (int)desiredPixelY);
                        }
                    }

                    BarWindow.Show();
                    BarWindow.Topmost = true;
                }
                catch (Exception ex)
                {
                    Log.Error("Could not display scroll capture floating bar.", ex);
                }
            });
        }

        private static async Task FinishRecordingAsync()
        {
            if (IsFinishing)
            {
                return;
            }

            IsFinishing = true;
            StopInputPolling();
            if (BarWindow != null)
            {
                BarWindow.SetHint("Stitching frames together...");
            }
            foreach (var win in ActiveWindows) 
            { 
                win.SetClickThrough(false); 
                win.IsHitTestVisible = true;
                if (win._recordingDot != null) { win._recordingDot.IsVisible = false; win._recordingDot.Classes.Remove("pulse"); }
                if (win._exitButton != null) win._exitButton.IsVisible = true;
            }
            BroadcastStatus("Building image", "Preparing pixels...");
            ScrollCaptureRecorder recorder = Recorder;
            Recorder = null;
            IsRecording = false;

            ImageSharpBgra result = null;
            try
            {
                if (recorder != null)
                {
                    if (!_hasScrolled || recorder.AcceptedFrames <= 1)
                    {
                        result = null;
                    }
                    else
                    {
                        var progress = new Progress<double>(p => 
                        {
                            BroadcastStatus("Building image", $"Stitching: {(int)(p * 100)}%");
                        });
                        result = await recorder.FinishAsync(progress).ConfigureAwait(false);
                    }
                    await recorder.DisposeAsync().ConfigureAwait(false);
                }

                if (result == null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (BarWindow != null)
                        {
                            BarWindow.Close();
                            BarWindow = null;
                        }
                        foreach (var win in ActiveWindows)
                        {
                            win.SetClickThrough(false);
                            win.IsHitTestVisible = true;
                            win.Show();
                            win.Activate();
                            win.Focus();
                        }
                        BroadcastStatus("NO SCROLLING DETECTED", "Keep pointer over window and scroll content · Enter to start");
                        IsFinishing = false;
                    });
                    return;
                }

                ImageSharpBgra clipboardImage = result.Clone(x => { });
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        RECT captureRect = RECT.FromXYWH(SelectedRect.Left, SelectedRect.Top, result.Width, result.Height);
                        CloseAllOverlays();
                        RestoreOwner();
                        CaptureHelper.OpenEditorForOwnedImage(result, captureRect);
                        result = null;
                    });
                    await CaptureHelper.CopyCaptureToClipboardAsync(clipboardImage).ConfigureAwait(false);
                }
                finally
                {
                    clipboardImage.Dispose();
                }
            }
            catch (Exception ex)
            {
                result?.Dispose();
                Log.Error("Scroll capture finish failed.", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (BarWindow != null)
                    {
                        BarWindow.Close();
                        BarWindow = null;
                    }
                    foreach (var win in ActiveWindows)
                    {
                        win.SetClickThrough(false);
                        win.IsHitTestVisible = true;
                        win.Show();
                        win.Activate();
                        win.Focus();
                    }
                    BroadcastStatus("Try again more slowly", "Enter to start");
                    IsFinishing = false;
                });
            }
            finally
            {
                result?.Dispose();
            }
        }

        private static async Task ExitModeAsync()
        {
            ScrollCaptureRecorder recorder = Recorder;
            Recorder = null;
            IsRecording = false;
            IsFinishing = false;
            if (recorder != null)
            {
                await recorder.CancelAsync().ConfigureAwait(false);
                await recorder.DisposeAsync().ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CloseAllOverlays();
                RestoreOwner();
            });
        }

        private void UpdateSelectionFromCursor()
        {
            POINT cursor = Win32WindowHelper.GetCursorPosition();

            if (_instructionBorder != null)
            {
                double scaling = RenderScaling;
                if (scaling <= 0) scaling = 1.0;
                double curX = (cursor.X - _screenBounds.X) / scaling;
                double curY = (cursor.Y - _screenBounds.Y) / scaling;
                double bannerWidth = _instructionBorder.Bounds.Width > 0 ? _instructionBorder.Bounds.Width : 350;
                double bannerHeight = _instructionBorder.Bounds.Height > 0 ? _instructionBorder.Bounds.Height : 50;

                double x = curX + 25;
                double y = curY + 25;

                if (x + bannerWidth > Width - 10)
                {
                    x = curX - bannerWidth - 25;
                }
                if (y + bannerHeight > Height - 10)
                {
                    y = curY - bannerHeight - 25;
                }

                x = Math.Max(10, Math.Min(Width - bannerWidth - 10, x));
                y = Math.Max(10, Math.Min(Height - bannerHeight - 10, y));

                Canvas.SetLeft(_instructionBorder, x);
                Canvas.SetTop(_instructionBorder, y);
            }

            IntPtr hwnd = Win32WindowHelper.GetRootWindowHandle(cursor);
            RECT rect = hwnd == IntPtr.Zero || !Win32WindowHelper.GetWindowRectActual(hwnd, out RECT windowRect) ? RECT.Empty : windowRect;
            
            if (hwnd != SelectedWindowHandle)
            {
                SelectedWindowHandle = hwnd;
                IsSelectedWindowElevated = Win32WindowHelper.IsWindowElevated(hwnd);
            }

            SelectedRect = rect;
            BroadcastSelection(rect);

            if (rect.IsEmpty)
            {
                BroadcastStatus("Point at a window", "Left-click window or press Enter to start (Esc exits)");
            }
            else if (IsSelectedWindowElevated)
            {
                BroadcastStatus("ELEVATED WINDOW DETECTED", "Run FreeSnip as Admin to capture");
            }
            else
            {
                BroadcastStatus("Window ready", "Left-click window or press Enter to start (Esc exits)");
            }
        }

        private static void BroadcastSelection(RECT rect)
        {
            for (int i = 0; i < ActiveWindows.Count; i++)
            {
                ActiveWindows[i].ShowSelection(rect);
            }
        }

        private static void BroadcastStatus(string instruction, string status)
        {
            for (int i = 0; i < ActiveWindows.Count; i++)
            {
                ActiveWindows[i].SetStatus(instruction, status);
            }
        }

        private void ShowSelection(RECT rect)
        {
            if (_highlightBorder == null)
            {
                return;
            }

            if (rect.IsEmpty)
            {
                _highlightBorder.IsVisible = false;
                return;
            }

            _highlightBorder.BorderBrush = IsSelectedWindowElevated ? Brushes.Red : Brushes.Lime;

            double scaling = RenderScaling;
            double left = (rect.Left - _screenBounds.X) / scaling;
            double top = (rect.Top - _screenBounds.Y) / scaling;
            double width = rect.Width / scaling;
            double height = rect.Height / scaling;
            if (left + width < 0 || top + height < 0 || left > Bounds.Width || top > Bounds.Height)
            {
                _highlightBorder.IsVisible = false;
                return;
            }

            _highlightBorder.IsVisible = true;
            Canvas.SetLeft(_highlightBorder, Math.Max(0, left));
            Canvas.SetTop(_highlightBorder, Math.Max(0, top));
            _highlightBorder.Width = Math.Min(Bounds.Width, left + width) - Math.Max(0, left);
            _highlightBorder.Height = Math.Min(Bounds.Height, top + height) - Math.Max(0, top);
        }

        private void SetStatus(string instruction, string status)
        {
            if (_instructionText != null)
            {
                _instructionText.Text = instruction;
                if (instruction == "ELEVATED WINDOW DETECTED" || instruction == "SCROLLING ACTIVE" || instruction == "ACCESS DENIED")
                    _instructionText.Foreground = new SolidColorBrush(Color.Parse("#FF7F50"));
                else
                    _instructionText.Foreground = Brushes.White;
            }

            if (_statusText != null)
            {
                _statusText.Text = status;
            }
        }

        private void LayoutFixedChrome()
        {
            if (_exitButton != null)
            {
                Canvas.SetRight(_exitButton, 20);
                Canvas.SetTop(_exitButton, 20);
            }
        }

        private static void CloseAllOverlays()
        {
            if (IsClosingAll)
            {
                return;
            }

            IsClosingAll = true;
            List<ScrollCaptureWindow> windows;
            lock (Sync)
            {
                windows = ActiveWindows.ToList();
                ActiveWindows.Clear();
            }

            foreach (ScrollCaptureWindow window in windows)
            {
                window.Close();
            }

            if (BarWindow != null)
            {
                BarWindow.Close();
                BarWindow = null;
            }

            IsClosingAll = false;
        }

        private static void RestoreOwner()
        {
            if (OwnerWindow != null && OwnerWasVisible)
            {
                OwnerWindow.Show();
                OwnerWindow.Activate();
            }

            OwnerWindow = null;
            OwnerWasVisible = false;
        }
    }
}
