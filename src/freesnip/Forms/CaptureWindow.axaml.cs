using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using freesnip.native.foundation;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using freesnip.native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using AvaloniaSize = Avalonia.Size;
using AvaloniaColor = Avalonia.Media.Color;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;

namespace freesnip.forms
{
    public partial class CaptureWindow : Window
    {
        private static readonly Avalonia.Input.Cursor HandCursor = new Avalonia.Input.Cursor(StandardCursorType.Hand);
        private static readonly Avalonia.Input.Cursor CrossCursor = new Avalonia.Input.Cursor(StandardCursorType.Cross);
        private static readonly ScaleTransform MagnifierScaleTransform = new ScaleTransform(2, 2);
        private static readonly RelativePoint MagnifierTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

        private static readonly log4net.ILog Log = LogHelper.GetLogger(typeof(CaptureWindow));
        private static List<CaptureWindow> _activeWindows = new List<CaptureWindow>();
        private static readonly object CaptureSessionLock = new object();
        private static bool _globalOcrMode;

        private const int MagneticThresholdPixels = 10;
        private static readonly long SnapProbeIntervalTicks = Stopwatch.Frequency / 60;
        private const double SnapProbeMoveThreshold = 8.0;
        private static readonly List<CaptureWindow> OverlayFanoutBuffer = new List<CaptureWindow>(8);
        private static bool _overlayFanoutBufferInUse;
        private long _snapProbeTicks;
        private POINT _snapProbePoint;
        private RECT _snapProbeRect;
        private IntPtr _snapProbeHandle;
        private bool _hasSnapProbe;
        private CoreConfiguration _cachedConfig;
        private int _lastDimensionWidth = int.MinValue;
        private int _lastDimensionHeight = int.MinValue;
        private bool _hasDimensionText;
        private Avalonia.Point _startPoint;
        private bool _isDragging;
        private bool _isWindowSnapActive;
        private Border _rubberband;
        private Border _highlightBorder;
        private bool _isPainterMode;
        private RECT _snappedRect = RECT.Empty;
        private IntPtr _snappedWindowHandle = IntPtr.Zero;
        private OcrInformation _ocrScanResult;
        private OcrWordSpatialIndex _ocrWordIndex;
        private readonly object _ocrStateLock = new object();
        private HashSet<OcrWord> _paintedWords = new HashSet<OcrWord>();
        private List<Border> _highlightRects = new List<Border>();
        private Border _ocrHoverPreview;
        private Canvas _mainCanvas;
        private Border _instructionBorder;
        private Avalonia.Controls.Image _backgroundControl;
        private Border _dimensionBadge;
        private Border _windowSnapBadge;
        private Avalonia.Controls.Shapes.Rectangle _guideLineVertical;
        private Avalonia.Controls.Shapes.Rectangle _guideLineHorizontal;
        private static readonly IBrush GuideLineBrush = new SolidColorBrush(AvaloniaColor.FromArgb(96, 0, 162, 237));
        private TextBlock _dimensionText;
        private TextBlock _copyText;
        private TextBlock _ocrText;
        private Border _ocrProcessingStatus;
        private CancellationTokenSource _ocrCts;
        private Task _ocrTask;
        private PixelRect _screenBounds;
        private bool _ocrFailed;
        private Avalonia.Media.Imaging.Bitmap _backgroundBitmap;
        private volatile bool _isWindowOpen;
        private Border _magnifierPanel;
        private Avalonia.Controls.Image _magnifierImage;

        private double Scaling => this.RenderScaling;

        private POINT ToScreenPixels(Avalonia.Point pos)
        {
            PixelPoint p = this.PointToScreen(pos);
            return new POINT(p.X, p.Y);
        }

        private RECT ToScreenRect(double leftDip, double topDip, double widthDip, double heightDip)
        {
            PixelPoint topLeft = this.PointToScreen(new Avalonia.Point(leftDip, topDip));
            PixelPoint bottomRight = this.PointToScreen(new Avalonia.Point(leftDip + widthDip, topDip + heightDip));
            return RECT.FromXYWH(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }

        private static void ForEachOtherOverlay(CaptureWindow source, Action<CaptureWindow> apply)
        {
            if (source == null || apply == null) return;
            List<CaptureWindow> targets = RentOverlaySnapshot(source, out bool pooled);
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    if (target != null && target._isWindowOpen) apply(target);
                }
            }
            finally
            {
                ReturnOverlaySnapshot(pooled);
            }
        }

        private static List<CaptureWindow> RentOverlaySnapshot(CaptureWindow source, out bool pooled)
        {
            List<CaptureWindow> targets;
            if (_overlayFanoutBufferInUse)
            {
                pooled = false;
                targets = new List<CaptureWindow>(8);
            }
            else
            {
                pooled = true;
                _overlayFanoutBufferInUse = true;
                targets = OverlayFanoutBuffer;
                targets.Clear();
            }

            lock (_activeWindows)
            {
                for (int i = 0; i < _activeWindows.Count; i++)
                {
                    var candidate = _activeWindows[i];
                    if (!ReferenceEquals(candidate, source)) targets.Add(candidate);
                }
            }

            return targets;
        }

        private static void ReturnOverlaySnapshot(bool pooled)
        {
            if (!pooled) return;
            OverlayFanoutBuffer.Clear();
            _overlayFanoutBufferInUse = false;
        }

        private double LocalDipX(double absoluteX) => (absoluteX - Position.X) / Scaling;
        private double LocalDipY(double absoluteY) => (absoluteY - Position.Y) / Scaling;

        private void SetDimensionText(int width, int height)
        {
            if (_dimensionText == null) return;
            if (_hasDimensionText && _lastDimensionWidth == width && _lastDimensionHeight == height) return;
            _lastDimensionWidth = width;
            _lastDimensionHeight = height;
            _hasDimensionText = true;
            _dimensionText.Text = string.Concat(width.ToString(NumberFormatInfo.InvariantInfo), " x ", height.ToString(NumberFormatInfo.InvariantInfo));
        }

        private bool IsPointInsideBounds(Avalonia.Point local)
        {
            double w = Bounds.Width > 0 ? Bounds.Width : Width;
            double h = Bounds.Height > 0 ? Bounds.Height : Height;
            return local.X >= 0 && local.Y >= 0 && local.X <= w && local.Y <= h;
        }

        private void PushRubberbandToOtherWindows()
        {
            if (_rubberband == null) return;
            bool visible = _rubberband.IsVisible;
            RECT absolute = visible
                ? ToScreenRect(Canvas.GetLeft(_rubberband), Canvas.GetTop(_rubberband), _rubberband.Width, _rubberband.Height)
                : RECT.Empty;
            ForEachOtherOverlay(this, w => w.ApplySharedRubberband(visible, absolute));
        }

        private void ApplySharedRubberband(bool visible, RECT absolute)
        {
            if (_rubberband == null) return;
            bool show = visible && !absolute.IsEmpty && absolute.Width > 0 && absolute.Height > 0;
            _rubberband.IsVisible = show;
            double left = LocalDipX(absolute.Left);
            double top = LocalDipY(absolute.Top);
            Canvas.SetLeft(_rubberband, left);
            Canvas.SetTop(_rubberband, top);
            _rubberband.Width = absolute.Width / Scaling;
            _rubberband.Height = absolute.Height / Scaling;
            if (_dimensionBadge != null && _dimensionText != null)
            {
                bool showBadge = show && absolute.Width > 10 && absolute.Height > 10;
                _dimensionBadge.IsVisible = showBadge;
                if (showBadge)
                {
                    SetDimensionText(absolute.Width, absolute.Height);
                    Canvas.SetLeft(_dimensionBadge, left);
                    double badgeY = top - 28;
                    if (badgeY < 0) badgeY = top + 5;
                    Canvas.SetTop(_dimensionBadge, badgeY);
                }
            }
        }

        private void PushSnapToOtherWindows()
        {
            bool active = _isWindowSnapActive && !_snappedRect.IsEmpty;
            RECT absolute = active ? _snappedRect : RECT.Empty;
            IntPtr handle = active ? _snappedWindowHandle : IntPtr.Zero;
            ForEachOtherOverlay(this, w => w.ApplySharedSnap(active, absolute, handle));
        }

        private void ApplySharedSnap(bool active, RECT absolute, IntPtr handle)
        {
            _isWindowSnapActive = active;
            _snappedRect = absolute;
            _snappedWindowHandle = handle;
            ApplySnapVisuals(active, absolute);
        }

        private void ApplySnapVisuals(bool active, RECT absolute)
        {
            if (_highlightBorder != null)
            {
                if (active && !absolute.IsEmpty)
                {
                    _highlightBorder.IsVisible = true;
                    Canvas.SetLeft(_highlightBorder, LocalDipX(absolute.Left));
                    Canvas.SetTop(_highlightBorder, LocalDipY(absolute.Top));
                    _highlightBorder.Width = absolute.Width / Scaling;
                    _highlightBorder.Height = absolute.Height / Scaling;
                }
                else
                {
                    _highlightBorder.IsVisible = false;
                }
            }
            if (_windowSnapBadge != null)
            {
                if (active && !absolute.IsEmpty)
                {
                    _windowSnapBadge.IsVisible = true;
                    Canvas.SetLeft(_windowSnapBadge, LocalDipX(absolute.Left) + 8);
                    Canvas.SetTop(_windowSnapBadge, Math.Max(8, LocalDipY(absolute.Top) + 8));
                }
                else
                {
                    _windowSnapBadge.IsVisible = false;
                }
            }
        }

        private void PushCursorToOtherWindows(Avalonia.Point local)
        {
            POINT absolute = ToScreenPixels(local);
            ForEachOtherOverlay(this, w => w.ApplySharedCursor(absolute.X, absolute.Y));
        }

        private void ApplySharedCursor(double absoluteX, double absoluteY)
        {
            var local = new Avalonia.Point(LocalDipX(absoluteX), LocalDipY(absoluteY));
            if (_isPainterMode)
            {
                HideGuides();
                if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
                return;
            }
            UpdateGuides(local);
            if (IsPointInsideBounds(local)) UpdateMagnifier(local);
            else if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
        }

        public CaptureWindow() : this(new PixelRect(0, 0, 1920, 1080), null)
        {
        }

        private static bool _captureSessionActive = false;
        private static DateTime _captureSessionStartTime = DateTime.MinValue;

        public static bool BeginCaptureSession()
        {
            freesnip.foundation.core.ScreenTintBypass.InvalidateCache();
            lock (CaptureSessionLock)
            {
                if (_captureSessionActive)
                {
                    bool hasActiveOverlays;
                    lock (_activeWindows) { hasActiveOverlays = _activeWindows.Count > 0; }
                    if (!hasActiveOverlays && (DateTime.UtcNow - _captureSessionStartTime).TotalSeconds > 3.0)
                    {
                        _captureSessionActive = false;
                    }
                    else
                    {
                        return false;
                    }
                }
                _captureSessionActive = true;
                _captureSessionStartTime = DateTime.UtcNow;
            }

            App.ForceRedTrayIcon(true, "CaptureSession");
            return true;
        }

        public static void EndCaptureSession()
        {
            lock (CaptureSessionLock)
            {
                _captureSessionActive = false;
            }

            App.ForceRedTrayIcon(false, "CaptureSession");
            App.ClearAllTrayHolds();
        }

        public CaptureWindow(PixelRect screenBounds, Avalonia.Media.Imaging.Bitmap background = null)
        {
            _screenBounds = screenBounds;
            InitializeComponent();
            freesnip.foundation.core.UiLayoutDirection.Apply(this);

            double scaling = 1.0;
            try
            {
                var screen = Screens.ScreenFromPoint(new PixelPoint(screenBounds.X, screenBounds.Y));
                if (screen != null) scaling = screen.Scaling;
            }
            catch { }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(screenBounds.X, screenBounds.Y);
            Width = screenBounds.Width / scaling;
            Height = screenBounds.Height / scaling;

            _mainCanvas = this.FindControl<Canvas>("MainCanvas");
            _rubberband = this.FindControl<Border>("Rubberband");
            _backgroundControl = this.FindControl<Avalonia.Controls.Image>("BackgroundImage");
            _highlightBorder = this.FindControl<Border>("HighlightBorder");
            _instructionBorder = this.FindControl<Border>("InstructionBorder");
            _dimensionBadge = this.FindControl<Border>("DimensionBadge");
            _windowSnapBadge = this.FindControl<Border>("WindowSnapBadge");
            _dimensionText = this.FindControl<TextBlock>("DimensionText");
            _copyText = this.FindControl<TextBlock>("CopyText");
            _ocrText = this.FindControl<TextBlock>("OcrText");
            _ocrProcessingStatus = this.FindControl<Border>("OcrProcessingStatus");
            _magnifierPanel = this.FindControl<Border>("MagnifierPanel");
            _magnifierImage = this.FindControl<Avalonia.Controls.Image>("MagnifierImage");
            if (_mainCanvas != null) _mainCanvas.Focusable = true;

            if (_mainCanvas != null)
            {
                _guideLineVertical = new Avalonia.Controls.Shapes.Rectangle { Fill = GuideLineBrush, IsHitTestVisible = false, IsVisible = false };
                _guideLineHorizontal = new Avalonia.Controls.Shapes.Rectangle { Fill = GuideLineBrush, IsHitTestVisible = false, IsVisible = false };
                int guideIndex = Math.Max(1, _mainCanvas.Children.IndexOf(_backgroundControl) + 1);
                _mainCanvas.Children.Insert(guideIndex, _guideLineVertical);
                _mainCanvas.Children.Insert(guideIndex + 1, _guideLineHorizontal);
            }
            
            if (_backgroundControl != null && background != null)
            {
                _backgroundBitmap = background;
                _backgroundControl.Source = background;
                _backgroundControl.Width = screenBounds.Width / scaling;
                _backgroundControl.Height = screenBounds.Height / scaling;
                
                if (_magnifierImage != null)
                {
                    _magnifierImage.Source = background;
                }
            }

            _isWindowOpen = true;

            AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            Closed += OnClosed;
            lock (_activeWindows) { _activeWindows.Add(this); }
            
            LayoutInstructionBanner(null);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            
            FocusForKeyboardCapture();
            
            LayoutInstructionBanner(null);
            LayoutOcrStatus();
            UiClipboard.Register(this, text => Clipboard?.SetTextAsync(text) ?? Task.FromException(new InvalidOperationException("Clipboard unavailable.")));
            if (_highlightBorder != null) _highlightBorder.IsVisible = false;
            if (_windowSnapBadge != null) _windowSnapBadge.IsVisible = false;
        }

        private void FocusForKeyboardCapture()
        {
            Activate();
            Focus();
            _mainCanvas?.Focus();
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isWindowOpen) return;
                Activate();
                Focus();
                _mainCanvas?.Focus();
            }, DispatcherPriority.Input);
        }

        private async void OnClosed(object sender, EventArgs e)
        {
            try
            {
                _isWindowOpen = false;
                UiClipboard.Unregister(this);
                CancelLocalPreemptiveOcr();
                await StopLocalOcrAsync().ConfigureAwait(true);

                bool isLastWindow;
                lock (_activeWindows)
                {
                    _activeWindows.Remove(this);
                    isLastWindow = _activeWindows.Count == 0;
                }

                if (isLastWindow)
                {
                    EndCaptureSession();
                    _globalOcrMode = false;
                    CaptureHelper.ClearFrozenSnapshot();
                    App.ClearAllTrayHolds();
                }
                
                if (_backgroundControl != null)
                {
                    _backgroundControl.Source = null;
                }

                _backgroundBitmap?.Dispose();
                _backgroundBitmap = null;
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(CaptureWindow)).Error("CaptureWindow.OnClosed cleanup failed", ex);
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
        }

        private void TryPostToUi(Action action)
        {
            if (!_isWindowOpen)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_isWindowOpen)
                {
                    action();
                }
            });
        }

        private CoreConfiguration Config => _cachedConfig ??= IniConfig.GetIniSection<CoreConfiguration>();

        private bool TryResolveSnapTarget(POINT absolute, out RECT rect, out IntPtr handle)
        {
            if (!Config.MagneticSnappingEnabled)
            {
                rect = RECT.Empty;
                handle = IntPtr.Zero;
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            if (_hasSnapProbe)
            {
                double dx = absolute.X - _snapProbePoint.X;
                double dy = absolute.Y - _snapProbePoint.Y;
                bool withinFrame = now - _snapProbeTicks < SnapProbeIntervalTicks;
                bool barelyMoved = (dx * dx + dy * dy) < SnapProbeMoveThreshold * SnapProbeMoveThreshold;
                if (withinFrame && barelyMoved)
                {
                    rect = _snapProbeRect;
                    handle = _snapProbeHandle;
                    return true;
                }
            }

            rect = Win32WindowHelper.GetSnappableWindow(absolute, out handle);
            _snapProbePoint = absolute;
            _snapProbeRect = rect;
            _snapProbeHandle = handle;
            _snapProbeTicks = now;
            _hasSnapProbe = true;
            return true;
        }

        private static void CloseAllCaptureOverlays()
        {
            List<CaptureWindow> toClose;
            lock (_activeWindows)
            {
                toClose = _activeWindows.ToList();
                _activeWindows.Clear();
            }

            foreach (var overlay in toClose)
            {
                overlay.CancelLocalPreemptiveOcr();
                overlay._isWindowOpen = false;
                overlay.Close();
            }

            EndCaptureSession();
        }

        private void LayoutInstructionBanner(Avalonia.Point? cursorPosition)
        {
            if (_instructionBorder == null || _mainCanvas == null) return;
            _instructionBorder.Measure(new AvaloniaSize(Width, Height));
            double bannerWidth = _instructionBorder.DesiredSize.Width;
            double bannerHeight = _instructionBorder.DesiredSize.Height;
            
            double left = (Width - bannerWidth) / 2;
            double top = Height - bannerHeight - 60;

            if (cursorPosition.HasValue)
            {
                Rect bannerRect = new Rect(left - 20, top - 20, bannerWidth + 40, bannerHeight + 40);
                if (bannerRect.Contains(cursorPosition.Value))
                {
                    top = 60;
                }
            }

            Canvas.SetLeft(_instructionBorder, left);
            Canvas.SetTop(_instructionBorder, top);
        }

        private void LayoutOcrStatus()
        {
            if (_ocrProcessingStatus == null || _mainCanvas == null) return;
            _ocrProcessingStatus.Measure(new AvaloniaSize(Width, Height));
            double left = (Width - _ocrProcessingStatus.DesiredSize.Width) / 2;
            double top = (Height - _ocrProcessingStatus.DesiredSize.Height) / 2;
            Canvas.SetLeft(_ocrProcessingStatus, left);
            Canvas.SetTop(_ocrProcessingStatus, top);
        }

        private readonly object LocalOcrLock = new object();
        private CancellationTokenSource _localOcrCts;
        private Task<OcrInformation> _localOcrTask;

        private Task<OcrInformation> StartLocalPreemptiveOcr(PixelRect targetRegion)
        {
            lock (LocalOcrLock)
            {
                if (_localOcrTask != null && !_localOcrTask.IsCompleted)
                {
                    return _localOcrTask;
                }

                SafeDisposeCts(_localOcrCts);
                _localOcrCts = new CancellationTokenSource();
                _localOcrTask = RunLocalPreemptiveOcrAsync(targetRegion, _localOcrCts.Token);
                return _localOcrTask;
            }
        }

        private void CancelLocalPreemptiveOcr()
        {
            CancellationTokenSource cts;
            Task<OcrInformation> task;
            lock (LocalOcrLock)
            {
                cts = _localOcrCts;
                task = _localOcrTask;
                _localOcrCts = null;
                _localOcrTask = null;
            }

            if (cts == null)
            {
                return;
            }

            try
            {
                SafeCancelCts(cts);
            }
            finally
            {
                if (task != null)
                {
                    _ = task.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        SafeDisposeCts(cts);
                    }, TaskScheduler.Default);
                }
                else
                {
                    SafeDisposeCts(cts);
                }
            }
        }

        private static IOcrProvider SelectOcrProvider()
        {
            var providers = SimpleServiceProvider.Current.GetAllInstances<IOcrProvider>();
            var config = IniConfig.GetIniSection<CoreConfiguration>();
            return OcrProviderSelector.Select(providers, config.OcrEngine);
        }

        private static async Task<OcrInformation> RunLocalPreemptiveOcrAsync(PixelRect targetRegion, CancellationToken cancellationToken)
        {
            var ocrProvider = SelectOcrProvider();
            if (ocrProvider == null)
            {
                return null;
            }

            ImageSharpImage image = null;
            try
            {
                var nativeRect = RECT.FromXYWH(targetRegion.X, targetRegion.Y, targetRegion.Width, targetRegion.Height);
                for (int attempt = 0; attempt < 20 && !CaptureHelper.IsFrozenSnapshotReady; attempt++)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (CaptureHelper.IsFrozenSnapshotReady)
                {
                    image = await Task.Run(() => CaptureHelper.GetFrozenSnapshot(nativeRect), cancellationToken).ConfigureAwait(false);
                }

                if (image == null)
                {
                    return null;
                }

                OcrInformation result = await ocrProvider.DoOcrAsync(image, cancellationToken).ConfigureAwait(false);
                result?.Offset(targetRegion.X, targetRegion.Y);
                return result;
            }
            finally
            {
                image?.Dispose();
            }
        }

        private async Task ObserveLocalPreemptiveOcrAsync(CancellationTokenSource cts)
        {
            try
            {
                TryPostToUi(() => { if (_isPainterMode && _ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = true; });
                OcrInformation result;
                try
                {
                    result = await StartLocalPreemptiveOcr(_screenBounds).WaitAsync(TimeSpan.FromSeconds(45), cts.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    BootstrapDebug.Log("Interactive OCR timed out after 45s.");
                    _ocrFailed = true;
                    TryPostToUi(() =>
                    {
                        if (_isPainterMode) ShowOcrWaitingState();
                    });
                    return;
                }
                if (result == null || cts.Token.IsCancellationRequested || !_isWindowOpen)
                {
                    _ocrFailed = result == null;
                    TryPostToUi(() =>
                    {
                        if (_isPainterMode) ShowOcrWaitingState();
                    });
                    return;
                }

                lock (_ocrStateLock)
                {
                    if (cts.Token.IsCancellationRequested || !_isWindowOpen)
                    {
                        return;
                    }

                    _ocrScanResult = result;
                    _ocrWordIndex = OcrWordSpatialIndex.Create(result.Words);
                    _ocrFailed = false;
                }

                TryPostToUi(() =>
                {
                    if (_isPainterMode) ShowOcrReadyState();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"OCR failed: {ex.Message}");
                _ocrFailed = true;
                TryPostToUi(() =>
                {
                    if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = false;
                    if (_isPainterMode)
                    {
                        var instruction = this.FindControl<TextBlock>("InstructionText");
                        if (instruction != null) instruction.Text = "Click text to capture";
                    }
                });
            }
            finally
            {
                bool ownsDisposal;
                lock (_ocrStateLock)
                {
                    ownsDisposal = ReferenceEquals(_ocrCts, cts);
                    if (ownsDisposal)
                    {
                        _ocrCts = null;
                        _ocrTask = null;
                    }
                }

                if (ownsDisposal) SafeDisposeCts(cts);
            }
        }

        private static void SafeCancelCts(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static void SafeDisposeCts(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task StopLocalOcrAsync()
        {
            Task task;
            CancellationTokenSource cts;
            lock (_ocrStateLock)
            {
                task = _ocrTask;
                cts = _ocrCts;
                _ocrTask = null;
                _ocrCts = null;
                _ocrScanResult = null;
                _ocrWordIndex = null;
            }

            SafeCancelCts(cts);

            if (task != null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            SafeDisposeCts(cts);
        }

        private bool IsOcrReady()
        {
            lock (_ocrStateLock) { return _ocrScanResult?.Words != null; }
        }

        private void ShowOcrWaitingState()
        {
            if (_ocrFailed)
            {
                if (_instructionBorder != null) _instructionBorder.IsVisible = true;
                var text = this.FindControl<TextBlock>("InstructionText");
                if (text != null) text.Text = "Text capture unavailable";
                if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = false;
                if (_ocrText != null)
                {
                    _ocrText.Text = "T = Exit text capture";
                    _ocrText.Foreground = Brushes.White;
                }
                return;
            }

            if (_instructionBorder != null) _instructionBorder.IsVisible = true;
            if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = true;
            LayoutOcrStatus();
            var instruction = this.FindControl<TextBlock>("InstructionText");
            if (instruction != null) instruction.Text = "Text capture loading...";
        }

        private void ShowOcrReadyState()
        {
            if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = false;
            if (!_isPainterMode) return;
            if (_instructionBorder != null) _instructionBorder.IsVisible = true;
            var instruction = this.FindControl<TextBlock>("InstructionText");
            if (instruction != null) instruction.Text = "Click text to capture";
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Log.Debug($"CaptureWindow KeyDown: {e.Key}");
            FocusForKeyboardCapture();
            if (e.Key == Key.Escape)
            {
                App.ForceRedTrayIcon(false, "RubberbandDrag");
                App.ForceRedTrayIcon(false, "CaptureOcr");
                App.ForceRedTrayIcon(false, "CaptureSession");
                e.Handled = true;
                CloseAllCaptureOverlays();
                return;
            }
            if (e.Key == Key.C) { e.Handled = true; CaptureAndCopyCurrentSelection(); return; }
            if (e.Key == Key.T || e.Key == Key.O)
            {
                Log.Info("OCR mode toggled via hotkey (scoping to local screens).");
                SetGlobalOcrMode(!_globalOcrMode);
                e.Handled = true;
            }
        }

        private static void SetGlobalOcrMode(bool enable)
        {
            _globalOcrMode = enable;
            if (enable)
            {
                App.ForceRedTrayIcon(true, "CaptureOcr");
            }
            else
            {
                App.ForceRedTrayIcon(false, "CaptureOcr");
            }

            List<CaptureWindow> windows;
            lock (_activeWindows) { windows = _activeWindows.ToList(); }
            foreach (var window in windows)
            {
                window.ApplyOcrMode(enable);
            }
        }

        private void ApplyOcrMode(bool enable)
        {
            if (_isPainterMode == enable) return;
            var text = this.FindControl<TextBlock>("InstructionText");
            if (enable) EnterOcrMode(text);
            else ExitOcrMode(text);
        }

        private void EnterOcrMode(TextBlock instructionText)
        {
            _isPainterMode = true;
            _ocrFailed = false;
            _isDragging = false;
            _isWindowSnapActive = false;
            _snappedRect = RECT.Empty;
            _snappedWindowHandle = IntPtr.Zero;

            lock (_ocrStateLock)
            {
                if (_ocrScanResult == null && _ocrCts == null)
                {
                    _ocrCts = new CancellationTokenSource();
                    _ocrTask = ObserveLocalPreemptiveOcrAsync(_ocrCts);
                }
                else if (_ocrScanResult == null && _ocrProcessingStatus != null)
                {
                    _ocrProcessingStatus.IsVisible = true;
                }
            }

            if (IsOcrReady())
            {
                if (instructionText != null) instructionText.Text = "Click text to capture";
                if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = false;
            }
            else
            {
                ShowOcrWaitingState();
            }
            if (_ocrText != null)
            {
                _ocrText.Text = "T = Exit text capture";
                _ocrText.Foreground = Brushes.White;
            }
            if (_copyText != null) _copyText.IsVisible = false;
            Cursor = HandCursor;
            _rubberband.IsVisible = false;
            if (_highlightBorder != null) _highlightBorder.IsVisible = false;
            if (_windowSnapBadge != null) _windowSnapBadge.IsVisible = false;
            if (_dimensionBadge != null) _dimensionBadge.IsVisible = false;
            if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
            HideGuides();
        }

        private void ExitOcrMode(TextBlock instructionText)
        {
            _isPainterMode = false;
            App.ForceRedTrayIcon(false, "CaptureOcr");
            _ = StopLocalOcrAsync();
            CancelLocalPreemptiveOcr();

            _isDragging = false;
            _isWindowSnapActive = false;
            _snappedRect = RECT.Empty;
            _snappedWindowHandle = IntPtr.Zero;
            ClearHighlights();
            _paintedWords.Clear();
            lock (_ocrStateLock)
            {
                _ocrScanResult = null;
                _ocrWordIndex = null;
            }

            if (_rubberband != null)
            {
                _rubberband.IsVisible = false;
                _rubberband.Width = 0;
                _rubberband.Height = 0;
            }
            if (_highlightBorder != null) _highlightBorder.IsVisible = false;
            if (_windowSnapBadge != null) _windowSnapBadge.IsVisible = false;
            if (_dimensionBadge != null) _dimensionBadge.IsVisible = false;
            if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = false;
            if (_instructionBorder != null) _instructionBorder.IsVisible = true;

            if (instructionText != null) instructionText.Text = "Drag to capture or hover window";
            if (_copyText != null) _copyText.IsVisible = true;
            if (_ocrText != null)
            {
                _ocrText.Text = "T = Text capture";
                _ocrText.Foreground = new SolidColorBrush(AvaloniaColor.Parse("#AAAAAA"));
            }
            Cursor = CrossCursor;
        }

        private void CaptureAndCopyCurrentSelection()
        {
            RECT target = RECT.Empty;
            IntPtr windowHandle = IntPtr.Zero;
            if (_isWindowSnapActive && !_snappedRect.IsEmpty)
            {
                target = _snappedRect;
                windowHandle = _snappedWindowHandle;
            }
            else if (_rubberband.IsVisible && _rubberband.Width > 5)
                target = ToScreenRect(Canvas.GetLeft(_rubberband), Canvas.GetTop(_rubberband), _rubberband.Width, _rubberband.Height);

            if (target.IsEmpty) return;
            _ = CaptureAndCopyCurrentSelectionAsync(target, windowHandle);
        }

        private async Task CaptureAndCopyCurrentSelectionAsync(RECT target, IntPtr windowHandle = default)
        {
            ImageSharpImage captured = await Task.Run(() => CaptureHelper.GetFrozenSnapshot(target)).ConfigureAwait(false);
            if (captured == null && windowHandle != IntPtr.Zero)
            {
                captured = await Task.Run(() => NativeCapture.CaptureWindow(windowHandle, target)).ConfigureAwait(false);
                if (captured != null && NativeCapture.IsLikelyBlankBlackFrame(captured))
                {
                    captured.Dispose();
                    captured = null;
                }
            }
            if (captured == null) captured = await Task.Run(() => NativeCapture.CaptureRegion(target)).ConfigureAwait(false);
            if (captured == null) return;

            if (IniConfig.GetIniSection<CoreConfiguration>().KeepBackup)
            {
                try
                {
                    var config = IniConfig.GetIniSection<CoreConfiguration>();
                    string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FreeSnip");
                    System.IO.Directory.CreateDirectory(tempDir);
                    string ext = config.OutputFileAllowPng ? "png" : "jpg";
                    string fileName = $"Capture_{DateTime.Now:yyyy-MM-dd HH_mm_ss_fff}.{ext}";
                    string fullPath = System.IO.Path.Combine(tempDir, fileName);
                    if (config.OutputFileAllowPng)
                    {
                        await Task.Run(() => captured.Save(fullPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder())).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(() => captured.Save(fullPath, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = config.OutputFileJpegQuality })).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[BACKUP_FAILURE] Could not write to temp folder during direct copy.", ex);
                }
            }

            await CaptureHelper.CopyCaptureToClipboardAsync(captured).ConfigureAwait(false);
            captured.Dispose();
            TryPostToUi(CloseAllCaptureOverlays);
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                App.ForceRedTrayIcon(false, "RubberbandDrag");
                App.ForceRedTrayIcon(false, "CaptureOcr");
                App.ForceRedTrayIcon(false, "CaptureSession");
                CloseAllCaptureOverlays();
                return;
            }

            _isDragging = true;
            if (!_isPainterMode)
            {
                Cursor = CrossCursor;
                App.ForceRedTrayIcon(true, "RubberbandDrag");
            }
            if (_instructionBorder != null) _instructionBorder.IsVisible = false;
            var pos = e.GetPosition(this);
            double scaling = Scaling;
            var absolutePos = ToScreenPixels(pos);
            RECT windowRect = Win32WindowHelper.GetRootWindowRect(absolutePos);
            double snapX = pos.X;
            double snapY = pos.Y;
            if (!windowRect.IsEmpty)
            {
                double relLeft = (windowRect.Left - Position.X) / scaling;
                double relTop = (windowRect.Top - Position.Y) / scaling;
                double relRight = (windowRect.Right - Position.X) / scaling;
                double relBottom = (windowRect.Bottom - Position.Y) / scaling;
                if (Math.Abs(pos.X - relLeft) < MagneticThresholdPixels) snapX = relLeft;
                else if (Math.Abs(pos.X - relRight) < MagneticThresholdPixels) snapX = relRight;
                if (Math.Abs(pos.Y - relTop) < MagneticThresholdPixels) snapY = relTop;
                else if (Math.Abs(pos.Y - relBottom) < MagneticThresholdPixels) snapY = relBottom;
            }
            _startPoint = new Avalonia.Point(snapX, snapY);
            if (_isPainterMode)
            {
                if (!IsOcrReady())
                {
                    _isDragging = false;
                    if (_instructionBorder != null) _instructionBorder.IsVisible = true;
                    ShowOcrWaitingState();
                    return;
                }

                ClearHighlights();
                _paintedWords.Clear();
                PaintWords(pos);
                return;
            }
            _rubberband.IsVisible = true;
            Canvas.SetLeft(_rubberband, _startPoint.X);
            Canvas.SetTop(_rubberband, _startPoint.Y);
            _rubberband.Width = 0;
            _rubberband.Height = 0;
            if (_highlightBorder != null) _highlightBorder.IsVisible = false;
            if (_windowSnapBadge != null) _windowSnapBadge.IsVisible = false;
            PushRubberbandToOtherWindows();
        }

        private void OnPointerMoved(object sender, PointerEventArgs e)
        {
            var currentPoint = e.GetPosition(this);
            double scaling = Scaling;
            
            if (_instructionBorder != null && !_isDragging)
            {
                LayoutInstructionBanner(currentPoint);
            }

            if (_isPainterMode)
            {
                HideGuides();
                if (!IsOcrReady())
                {
                    ShowOcrWaitingState();
                    return;
                }

                if (_isDragging)
                {
                    PaintWords(currentPoint);
                }
                else
                {
                    ShowOcrHoverPreview(currentPoint);
                }
                return;
            }

            if (!_isDragging)
            {
                UpdateGuides(currentPoint);
                UpdateMagnifier(currentPoint);
                CheckMagneticSnap(currentPoint);
                PushCursorToOtherWindows(currentPoint);
                PushSnapToOtherWindows();
                return;
            }
            var absoluteCurrent = ToScreenPixels(currentPoint);
            TryResolveSnapTarget(absoluteCurrent, out RECT snapRect, out IntPtr dragSnapWindowHandle);
            double snappedX = currentPoint.X;
            double snappedY = currentPoint.Y;
            if (!snapRect.IsEmpty)
            {
                double relLeft = (snapRect.Left - Position.X) / scaling;
                double relTop = (snapRect.Top - Position.Y) / scaling;
                double relRight = (snapRect.Right - Position.X) / scaling;
                double relBottom = (snapRect.Bottom - Position.Y) / scaling;
                if (Math.Abs(currentPoint.X - relLeft) < MagneticThresholdPixels) snappedX = relLeft;
                else if (Math.Abs(currentPoint.X - relRight) < MagneticThresholdPixels) snappedX = relRight;
                if (Math.Abs(currentPoint.Y - relTop) < MagneticThresholdPixels) snappedY = relTop;
                else if (Math.Abs(currentPoint.Y - relBottom) < MagneticThresholdPixels) snappedY = relBottom;
                bool isWholeWindowSnap = (Math.Abs(_startPoint.X - relLeft) < 1) && (Math.Abs(_startPoint.Y - relTop) < 1) && (Math.Abs(snappedX - relRight) < 1) && (Math.Abs(snappedY - relBottom) < 1);
                if (isWholeWindowSnap)
                {
                    _isWindowSnapActive = true;
                    _snappedRect = snapRect;
                    _snappedWindowHandle = dragSnapWindowHandle;
                    Cursor = HandCursor;
                }
                else { _isWindowSnapActive = false; _snappedRect = RECT.Empty; _snappedWindowHandle = IntPtr.Zero; Cursor = CrossCursor; }
                ApplySnapVisuals(_isWindowSnapActive, _snappedRect);
            }
            else if (_isWindowSnapActive)
            {
                _isWindowSnapActive = false;
                _snappedRect = RECT.Empty;
                _snappedWindowHandle = IntPtr.Zero;
                Cursor = CrossCursor;
                ApplySnapVisuals(false, RECT.Empty);
            }
            double x = Math.Min(_startPoint.X, snappedX);
            double y = Math.Min(_startPoint.Y, snappedY);
            double w = Math.Abs(_startPoint.X - snappedX);
            double h = Math.Abs(_startPoint.Y - snappedY);
            Canvas.SetLeft(_rubberband, x);
            Canvas.SetTop(_rubberband, y);
            _rubberband.Width = w;
            _rubberband.Height = h;

            if (_dimensionBadge != null && _dimensionText != null)
            {
                _dimensionBadge.IsVisible = w > 10 && h > 10;
                SetDimensionText((int)Math.Round(w * scaling), (int)Math.Round(h * scaling));
                Canvas.SetLeft(_dimensionBadge, x);
                double badgeY = y - 28;
                if (badgeY < 0) badgeY = y + 5;
                Canvas.SetTop(_dimensionBadge, badgeY);
            }

            bool cursorOnThisOverlay = IsPointInsideBounds(currentPoint);
            UpdateGuides(currentPoint);
            if (cursorOnThisOverlay) UpdateMagnifier(currentPoint);
            else if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
            PushRubberbandToOtherWindows();
            PushSnapToOtherWindows();
            PushCursorToOtherWindows(currentPoint);
        }

        private void UpdateMagnifier(Avalonia.Point pos)
        {
            if (_isPainterMode || _magnifierPanel == null)
            {
                if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
                return;
            }

            _magnifierPanel.IsVisible = true;

            double magSize = !double.IsNaN(_magnifierPanel.Width) && _magnifierPanel.Width > 0 ? _magnifierPanel.Width : 160;
            double offset = 30;
            var target = ChooseMagnifierPosition(pos, magSize, offset, GetMagnifierAvoidRect(12, pos, magSize));

            Canvas.SetLeft(_magnifierPanel, target.X);
            Canvas.SetTop(_magnifierPanel, target.Y);

            if (_magnifierImage != null)
            {
                double scaling = Scaling;
                if (_magnifierImage.Width != _screenBounds.Width)
                {
                    _magnifierImage.Width = _screenBounds.Width;
                    _magnifierImage.Height = _screenBounds.Height;
                    _magnifierImage.RenderTransform = MagnifierScaleTransform;
                    _magnifierImage.RenderTransformOrigin = MagnifierTransformOrigin;
                }
                Canvas.SetLeft(_magnifierImage, magSize / 2 - pos.X * scaling * 2);
                Canvas.SetTop(_magnifierImage, magSize / 2 - pos.Y * scaling * 2);
            }
        }

        private Rect? GetMagnifierAvoidRect(double margin, Avalonia.Point cursor, double magSize)
        {
            if (_rubberband != null && _rubberband.IsVisible && _rubberband.Width > 1 && _rubberband.Height > 1)
            {
                double left = Canvas.GetLeft(_rubberband);
                double top = Canvas.GetTop(_rubberband);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                return InflateRect(new Rect(left, top, _rubberband.Width, _rubberband.Height), margin);
            }

            return new Rect(cursor.X - magSize / 2, cursor.Y - magSize / 2, magSize, magSize);
        }

        private Rect ChooseMagnifierPosition(Avalonia.Point cursor, double magSize, double offset, Rect? avoidRect)
        {
            double canvasWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            double canvasHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            if (double.IsNaN(canvasWidth) || canvasWidth <= 0) canvasWidth = _screenBounds.Width / Math.Max(Scaling, 1.0);
            if (double.IsNaN(canvasHeight) || canvasHeight <= 0) canvasHeight = _screenBounds.Height / Math.Max(Scaling, 1.0);

            Rect best = new Rect();
            double bestScore = double.MaxValue;

            if (avoidRect.HasValue)
            {
                var avoid = avoidRect.Value;
                double ar = avoid.X + avoid.Width;
                double ab = avoid.Y + avoid.Height;

                void Eval(double left, double top)
                {
                    Rect candidate = new Rect(left, top, magSize, magSize);
                    Rect clamped = ClampRect(candidate, canvasWidth, canvasHeight);
                    double overlapPenalty = avoidRect.HasValue ? RectOverlapArea(clamped, avoidRect.Value) * 100000 : 0;
                    double clampPenalty = Distance(candidate.X, candidate.Y, clamped.X, clamped.Y) * 10;
                    double cursorPenalty = Distance(cursor.X, cursor.Y, clamped.X + clamped.Width / 2, clamped.Y + clamped.Height / 2);
                    double avoidBonus = avoidRect.HasValue ? RectDistance(clamped, avoidRect.Value) * 0.25 : 0;
                    double score = overlapPenalty + clampPenalty + cursorPenalty - avoidBonus;
                    if (score < bestScore) { bestScore = score; best = clamped; }
                }

                Eval(ar + offset, cursor.Y - magSize / 2);
                Eval(avoid.X - magSize - offset, cursor.Y - magSize / 2);
                Eval(cursor.X - magSize / 2, ab + offset);
                Eval(cursor.X - magSize / 2, avoid.Y - magSize - offset);
                Eval(ar + offset, ab + offset);
                Eval(avoid.X - magSize - offset, ab + offset);
                Eval(ar + offset, avoid.Y - magSize - offset);
                Eval(avoid.X - magSize - offset, avoid.Y - magSize - offset);
            }

            void EvalDirect(double left, double top)
            {
                Rect candidate = new Rect(left, top, magSize, magSize);
                Rect clamped = ClampRect(candidate, canvasWidth, canvasHeight);
                double overlapPenalty = avoidRect.HasValue ? RectOverlapArea(clamped, avoidRect.Value) * 100000 : 0;
                double clampPenalty = Distance(candidate.X, candidate.Y, clamped.X, clamped.Y) * 10;
                double cursorPenalty = Distance(cursor.X, cursor.Y, clamped.X + clamped.Width / 2, clamped.Y + clamped.Height / 2);
                double avoidBonus = avoidRect.HasValue ? RectDistance(clamped, avoidRect.Value) * 0.25 : 0;
                double score = overlapPenalty + clampPenalty + cursorPenalty - avoidBonus;
                if (score < bestScore) { bestScore = score; best = clamped; }
            }

            EvalDirect(cursor.X + offset, cursor.Y + offset);
            EvalDirect(cursor.X - magSize - offset, cursor.Y + offset);
            EvalDirect(cursor.X + offset, cursor.Y - magSize - offset);
            EvalDirect(cursor.X - magSize - offset, cursor.Y - magSize - offset);

            return best;
        }

        private static Rect InflateRect(Rect rect, double margin)
        {
            return new Rect(rect.X - margin, rect.Y - margin, rect.Width + margin * 2, rect.Height + margin * 2);
        }

        private static Rect ClampRect(Rect rect, double canvasWidth, double canvasHeight)
        {
            double maxLeft = Math.Max(0, canvasWidth - rect.Width);
            double maxTop = Math.Max(0, canvasHeight - rect.Height);
            return new Rect(Math.Clamp(rect.X, 0, maxLeft), Math.Clamp(rect.Y, 0, maxTop), rect.Width, rect.Height);
        }

        private static double RectOverlapArea(Rect a, Rect b)
        {
            double left = Math.Max(a.X, b.X);
            double top = Math.Max(a.Y, b.Y);
            double right = Math.Min(a.X + a.Width, b.X + b.Width);
            double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            if (right <= left || bottom <= top) return 0;
            return (right - left) * (bottom - top);
        }

        private static double RectDistance(Rect a, Rect b)
        {
            double dx = Math.Max(Math.Max(b.X - (a.X + a.Width), a.X - (b.X + b.Width)), 0);
            double dy = Math.Max(Math.Max(b.Y - (a.Y + a.Height), a.Y - (b.Y + b.Height)), 0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private async Task OnPointerReleasedAsync(PointerReleasedEventArgs e)
        {
            var endPoint = e.GetPosition(this);
            double deltaX = Math.Abs(endPoint.X - _startPoint.X);
            double deltaY = Math.Abs(endPoint.Y - _startPoint.Y);
            bool isIntentionalDrag = deltaX > 5 || deltaY > 5;
            _isDragging = false;
            App.ForceRedTrayIcon(false, "RubberbandDrag");
            
            if (!_isPainterMode && _instructionBorder != null) _instructionBorder.IsVisible = true;
            if (_magnifierPanel != null) _magnifierPanel.IsVisible = false;
            HideGuides();
            ForEachOtherOverlay(this, w =>
            {
                w.HideGuides();
                if (w._magnifierPanel != null) w._magnifierPanel.IsVisible = false;
            });

            if (_isPainterMode)
            {
                if (!IsOcrReady())
                {
                    ShowOcrWaitingState();
                    if (_instructionBorder != null) _instructionBorder.IsVisible = true;
                    return;
                }

                if (_paintedWords.Count > 0)
                {
                    var handler = SimpleServiceProvider.Current.GetInstance<IOcrResultHandler>(true);
                    if (handler != null)
                    {
                        string text = freesnip.helpers.OcrTextLayout.BuildVisualSelectionText(_paintedWords);
                        try { await handler.HandleOcrResult(text).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            Log.Error("OCR result was not delivered; keeping the capture open.", ex);
                            TryPostToUi(() => freesnip.editor.forms.NotificationOverlayWindow.ShowNotification("COPY FAILED - PLEASE RETRY", this));
                            return;
                        }
                    }
                    TryPostToUi(CloseAllCaptureOverlays);
                }
                else
                {
                    if (_instructionBorder != null) _instructionBorder.IsVisible = true;
                }
                return;
            }

            if (!isIntentionalDrag)
            {
                CheckMagneticSnap(_startPoint);
                if (_isWindowSnapActive && !_snappedRect.IsEmpty)
                {
                    StartCaptureAndClose(_snappedRect, _snappedWindowHandle);
                    return;
                }
            }

            if (_isWindowSnapActive && _snappedRect.Width > 0 && isIntentionalDrag) { StartCaptureAndClose(_snappedRect, _snappedWindowHandle); return; }
            if (isIntentionalDrag)
            {
                var captureRect = ToScreenRect(Canvas.GetLeft(_rubberband), Canvas.GetTop(_rubberband), _rubberband.Width, _rubberband.Height);
                if (captureRect.Width > 2 && captureRect.Height > 2) { StartCaptureAndClose(captureRect); }
            }
        }

        private void OnPointerReleased(object sender, PointerReleasedEventArgs e) { _ = OnPointerReleasedAsync(e); }

        private void StartCaptureAndClose(RECT rect, IntPtr windowHandle = default)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            _ = CaptureAfterOverlaysHiddenAsync(rect, windowHandle);
        }

        private static async Task CloseAllCaptureOverlaysAsync()
        {
            var allClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                List<CaptureWindow> toClose;
                lock (_activeWindows)
                {
                    toClose = _activeWindows.ToList();
                    _activeWindows.Clear();
                }

                if (toClose.Count == 0)
                {
                    allClosed.TrySetResult(true);
                    return;
                }

                int remaining = toClose.Count;
                foreach (var overlay in toClose)
                {
                    var window = overlay;
                    window.CancelLocalPreemptiveOcr();
                    window._isWindowOpen = false;
                    EventHandler onClosed = null;
                    onClosed = (_, _) =>
                    {
                        window.Closed -= onClosed;
                        if (Interlocked.Decrement(ref remaining) == 0) allClosed.TrySetResult(true);
                    };
                    window.Closed += onClosed;
                    window.Close();
                }
            });

            var completed = await Task.WhenAny(allClosed.Task, Task.Delay(1000)).ConfigureAwait(false);
            if (completed == allClosed.Task)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        private static async Task CaptureAfterOverlaysHiddenAsync(RECT rect, IntPtr windowHandle = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"[STEP:START] CaptureAfterOverlaysHiddenAsync - Rect: ({rect.X},{rect.Y},{rect.Width}x{rect.Height}), WindowHandle: {windowHandle}");
            ImageSharpImage owned = null;
            ImageSharpImage frozenCaptured = null;
            ImageSharpImage imageForEditor = null;
            try
            {
                var nativeRect = RECT.FromXYWH(rect.X, rect.Y, rect.Width, rect.Height);

                if (windowHandle != IntPtr.Zero)
                {
                    frozenCaptured = await Task.Run(() => NativeCapture.CaptureWindow(windowHandle, null)).ConfigureAwait(false);
                    if (frozenCaptured != null && NativeCapture.IsLikelyBlankBlackFrame(frozenCaptured))
                    {
                        frozenCaptured.Dispose();
                        frozenCaptured = null;
                    }
                }

                if (frozenCaptured == null)
                {
                    frozenCaptured = await Task.Run(() => CaptureHelper.GetFrozenSnapshot(nativeRect)).ConfigureAwait(false);
                }
                if (frozenCaptured == null)
                {
                    frozenCaptured = await Task.Run(() => NativeCapture.CaptureRegion(nativeRect)).ConfigureAwait(false);
                }

                await CloseAllCaptureOverlaysAsync().ConfigureAwait(false);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    frozenCaptured?.Dispose();
                    CaptureHelper.ClearFrozenSnapshot();
                    sw.Stop();
                    Log.Info($"[STEP:INFO] CaptureAfterOverlaysHiddenAsync ({sw.ElapsedMilliseconds}ms) - Selection rect was empty; capture cancelled.");
                    return;
                }

                CaptureHelper.RememberRegion(rect);
                owned = await Task.Run(() =>
                {
                    ImageSharpImage captured = frozenCaptured;
                    if (captured == null) return null;
                    var clone = captured.Clone(x => { });
                    captured.Dispose();

                    if (IniConfig.GetIniSection<CoreConfiguration>().KeepBackup)
                    {
                        try
                        {
                            var config = IniConfig.GetIniSection<CoreConfiguration>();
                            string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                            Directory.CreateDirectory(tempDir);
                            string ext = config.OutputFileAllowPng ? "png" : "jpg";
                            string fileName = $"Raw_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.{ext}";
                            if (config.OutputFileAllowPng)
                            {
                                clone.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                            }
                            else
                            {
                                clone.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = config.OutputFileJpegQuality });
                            }
                        }
                        catch { }
                    }
                    return clone;
                }).ConfigureAwait(false);

                if (owned == null) 
                {
                    CaptureHelper.ClearFrozenSnapshot();
                    sw.Stop();
                    Log.Error($"[STEP:FAIL] CaptureAfterOverlaysHiddenAsync ({sw.ElapsedMilliseconds}ms) - Failed to extract captured region image.");
                    return;
                }

                await CaptureHelper.CopyCaptureToClipboardAsync(owned).ConfigureAwait(false);

                imageForEditor = owned;
                owned = null;
                string targetTitle = windowHandle != IntPtr.Zero ? Win32WindowHelper.GetWindowTitle(windowHandle) : null;
                if (string.IsNullOrWhiteSpace(targetTitle)) targetTitle = CaptureHelper.LastActiveWindowTitle;
                if (imageForEditor != null && (imageForEditor.Width != rect.Width || imageForEditor.Height != rect.Height))
                {
                    rect = RECT.FromXYWH(rect.X, rect.Y, imageForEditor.Width, imageForEditor.Height);
                }
                try
                {
                    Log.Info($"[STEP:START] CaptureAfterOverlaysHiddenAsync - Launching editor for image ({imageForEditor.Width}x{imageForEditor.Height}).");
                    await Dispatcher.UIThread.InvokeAsync(() => ShowEditorForOwnedImageAsync(imageForEditor, rect, targetTitle));
                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] CaptureAfterOverlaysHiddenAsync ({sw.ElapsedMilliseconds}ms) - Editor window loaded successfully.");
                }
                finally
                {
                    CaptureHelper.ClearFrozenSnapshot();
                }
            }
            catch (Exception ex)
            {
                frozenCaptured?.Dispose();
                imageForEditor?.Dispose();
                owned?.Dispose();
                CaptureHelper.ClearFrozenSnapshot();
                sw.Stop();
                Log.Fatal($"[STEP:FAIL] CaptureAfterOverlaysHiddenAsync ({sw.ElapsedMilliseconds}ms) - Capture failed.", ex);
            }
            finally
            {
                EndCaptureSession();
            }
        }

        private static async Task ShowEditorForOwnedImageAsync(ImageSharpImage image, RECT rect, string targetTitle = null)
        {
            ImageSharpImage imageForEditor = image;
            freesnip.editor.forms.ImageEditorWindow editor = null;
            try
            {
                editor = new freesnip.editor.forms.ImageEditorWindow();

                await editor.SetImageAsync(imageForEditor, rect, targetTitle ?? CaptureHelper.LastActiveWindowTitle).ConfigureAwait(true);
                imageForEditor = null;
                editor.Show();
                App.ClearAllTrayHolds();
            }
            catch (Exception ex)
            {
                App.ClearAllTrayHolds();
                imageForEditor?.Dispose();
                editor?.Close();
                Log.Fatal("ShowEditorForOwnedImage failed.", ex);
                throw;
            }
        }

        private void UpdateGuides(Avalonia.Point pos)
        {
            if (_guideLineVertical == null || _guideLineHorizontal == null) return;
            if (_isPainterMode) { HideGuides(); return; }

            double canvasWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            double canvasHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            if (double.IsNaN(canvasWidth) || canvasWidth <= 0) canvasWidth = _screenBounds.Width / Math.Max(Scaling, 1.0);
            if (double.IsNaN(canvasHeight) || canvasHeight <= 0) canvasHeight = _screenBounds.Height / Math.Max(Scaling, 1.0);

            double thickness = 1.0 / Math.Max(Scaling, 1.0);

            _guideLineVertical.Width = thickness;
            _guideLineVertical.Height = canvasHeight;
            Canvas.SetLeft(_guideLineVertical, pos.X - thickness / 2);
            Canvas.SetTop(_guideLineVertical, 0);

            _guideLineHorizontal.Height = thickness;
            _guideLineHorizontal.Width = canvasWidth;
            Canvas.SetTop(_guideLineHorizontal, pos.Y - thickness / 2);
            Canvas.SetLeft(_guideLineHorizontal, 0);

            _guideLineVertical.IsVisible = true;
            _guideLineHorizontal.IsVisible = true;
        }

        private void HideGuides()
        {
            if (_guideLineVertical != null) _guideLineVertical.IsVisible = false;
            if (_guideLineHorizontal != null) _guideLineHorizontal.IsVisible = false;
        }

        private void CheckMagneticSnap(Avalonia.Point pos)
        {
            var absolutePos = ToScreenPixels(pos);
            TryResolveSnapTarget(absolutePos, out RECT windowRect, out IntPtr windowHandle);
            if (windowRect.IsEmpty || windowRect.Width <= 0 || windowRect.Height <= 0)
            {
                _snappedRect = RECT.Empty;
                _snappedWindowHandle = IntPtr.Zero;
                _isWindowSnapActive = false;
                Cursor = CrossCursor;
                ApplySnapVisuals(false, RECT.Empty);
                PushSnapToOtherWindows();
                return;
            }
            _snappedRect = windowRect;
            _snappedWindowHandle = windowHandle;
            _isWindowSnapActive = true;
            Cursor = HandCursor;
            ApplySnapVisuals(true, windowRect);
            PushSnapToOtherWindows();
        }

        private void PaintWords(Avalonia.Point pos)
        {
            OcrInformation scan;
            OcrWordSpatialIndex index;
            lock (_ocrStateLock) { scan = _ocrScanResult; index = _ocrWordIndex; }
            if (scan?.Words == null)
            {
                if (_ocrProcessingStatus != null) _ocrProcessingStatus.IsVisible = true;
                return;
            }
            double scaling = Scaling;
            var absolutePos = ToScreenPixels(pos);
            var word = index?.FindContaining(absolutePos);
            if (word == null)
            {
                return;
            }

            if (!_paintedWords.Add(word)) return;
            HideOcrHoverPreview();
            var highlight = new Border { Background = new SolidColorBrush(AvaloniaColor.FromArgb(102, 255, 255, 0)), Width = word.Bounds.Width / scaling, Height = word.Bounds.Height / scaling, IsHitTestVisible = false };
            Canvas.SetLeft(highlight, (word.Bounds.Left - Position.X) / scaling);
            Canvas.SetTop(highlight, (word.Bounds.Top - Position.Y) / scaling);
            _mainCanvas.Children.Add(highlight);
            _highlightRects.Add(highlight);
        }

        private void ShowOcrHoverPreview(Avalonia.Point pos)
        {
            OcrInformation scan;
            OcrWordSpatialIndex index;
            lock (_ocrStateLock) { scan = _ocrScanResult; index = _ocrWordIndex; }
            if (scan?.Words == null)
            {
                HideOcrHoverPreview();
                return;
            }

            double scaling = Scaling;
            var absolutePos = ToScreenPixels(pos);
            var word = index?.FindContaining(absolutePos);
            if (word == null)
            {
                HideOcrHoverPreview();
                return;
            }

            if (_ocrHoverPreview == null)
            {
                _ocrHoverPreview = new Border
                {
                    Background = new SolidColorBrush(AvaloniaColor.FromArgb(42, 0, 120, 255)),
                    BorderBrush = Brushes.DeepSkyBlue,
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false
                };
                _mainCanvas.Children.Add(_ocrHoverPreview);
            }

            _ocrHoverPreview.Width = Math.Max(1, word.Bounds.Width / scaling);
            _ocrHoverPreview.Height = Math.Max(1, word.Bounds.Height / scaling);
            Canvas.SetLeft(_ocrHoverPreview, (word.Bounds.Left - Position.X) / scaling);
            Canvas.SetTop(_ocrHoverPreview, (word.Bounds.Top - Position.Y) / scaling);
            _ocrHoverPreview.IsVisible = true;
        }

        private void HideOcrHoverPreview()
        {
            if (_ocrHoverPreview != null)
            {
                _ocrHoverPreview.IsVisible = false;
            }
        }

        private void ClearHighlights() { HideOcrHoverPreview(); foreach (var rect in _highlightRects) { _mainCanvas.Children.Remove(rect); } _highlightRects.Clear(); }
    }
}
