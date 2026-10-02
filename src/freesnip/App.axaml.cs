using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using freesnip.native;
using freesnip.native.foundation;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.foundation.Interfaces;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using freesnip.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace freesnip
{
    public class App : Application
    {
        private static TrayIcon _trayIcon;
        private static WindowIcon _blueIcon;
        private static WindowIcon _redIcon;
        private static IClassicDesktopStyleApplicationLifetime _desktop;
        private static CancellationTokenSource _mainAppCts = new CancellationTokenSource();
        private static helpers.ResourceMutex s_instanceMutex;
        private static helpers.InstanceIpcServer s_ipcServer;
        public static bool IsNoTrayMode { get; set; }

        internal static void SetInstanceArbitrator(helpers.ResourceMutex arbitrator)
        {
            s_instanceMutex = arbitrator;
        }

        public override void Initialize() { AvaloniaXamlLoader.Load(this); }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _desktop = desktop;
                int uiExceptionCascadeCount = 0;
                DateTime lastUiExceptionTime = DateTime.MinValue;
                Dispatcher.UIThread.UnhandledException += (s, e) =>
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastUiExceptionTime).TotalSeconds < 1.0)
                    {
                        uiExceptionCascadeCount++;
                    }
                    else
                    {
                        uiExceptionCascadeCount = 1;
                    }
                    lastUiExceptionTime = now;

                    if (uiExceptionCascadeCount <= 3)
                    {
                        try
                        {
                            LogHelper.LogCrash("Dispatcher.UnhandledException", e.Exception, e.Exception);
                            LogHelper.GetLogger(typeof(App)).Error("UI thread unhandled exception (handled)", e.Exception);
                        }
                        catch (Exception logEx)
                        {
                            BootstrapDebug.Log($"Logging failed: {logEx.Message}");
                        }
                    }
                    else if (uiExceptionCascadeCount == 4)
                    {
                        LogHelper.GetLogger(typeof(App)).Warn("Rapid UI thread exception cascade detected; throttling further crash logs.");
                    }
                    e.Handled = true;
                };
                desktop.MainWindow = null;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = InitializeApplicationAsync(desktop, _mainAppCts.Token);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private async Task InitializeApplicationAsync(IClassicDesktopStyleApplicationLifetime desktop, CancellationToken cancellationToken)
        {
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogHelper.LogCrash("App.UnobservedTaskException", e.Exception, e.Exception);
                LogHelper.GetLogger(typeof(App)).Error("Unobserved Task Exception", e.Exception);
                e.SetObserved();
            };

            var ocrProviders = new List<IOcrProvider>();
            try
            {
                LogHelper.InitializeLog4Net();
                var log = LogHelper.GetLogger(typeof(App));
                log.Info("--- Application Bootstrap Starting ---");
                log.Info($"Executable Path: {RuntimePathHelper.ExecutablePath}");

                string[] args = desktop.Args ?? Array.Empty<string>();
                log.Info($"Command line arguments: {string.Join(" ", args)}");

                if (DeploymentLifecycle.IsLifecycleCommand(args))
                {
                    log.Info("Running deployment lifecycle command...");
                    int exitCode = await DeploymentLifecycle.RunLifecycleCommandAsync(args);
                    log.Info($"Deployment command finished with exit code: {exitCode}");
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));
                    return;
                }
                InitializePersistentConfiguration();
                ExecutionTrace.Start();
                var options = FreeSnipCommandLine.Parse(args);
                UiClipboard.RegisterGetter(() => desktop.MainWindow?.Clipboard ?? (desktop.Windows.FirstOrDefault()?.Clipboard));
                if (s_instanceMutex == null)
                {
                    s_instanceMutex = helpers.ResourceMutex.Create(helpers.ResourceMutex.SingleInstanceArbitratorName, "FreeSnip instance", true);
                }
                if (!s_instanceMutex.IsLocked)
                {
                    log.Warn("Another instance of FreeSnip is already running.");
                    Win32WindowHelper.AllowSetForegroundWindow(Win32WindowHelper.ASFW_ANY);
                    await InstanceIpcClient.SendRelayCommandAsync(args, timeoutMs: 500).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
                    return;
                }

                StartIpcServer(cancellationToken);
                    SimpleServiceProvider.Current.AddService<IAppLifecycleCoordinator>(new AppLifecycleCoordinator());
                    SimpleServiceProvider.Current.AddService<ISettingsService>(new SettingsService());
                    SimpleServiceProvider.Current.AddService<IOcrResultHandler>(new OcrResultHandler());
                    SimpleServiceProvider.Current.AddService<IScrollCaptureLauncher>(new ScrollCaptureLauncher());
#if USE_TESSERACT
                    log.Info("Using Tesseract OCR Provider with Windows OCR fallback.");
                    var tesseractProvider = new native.TesseractOcrProvider();
                    ocrProviders.Add(new native.MixedLanguageOcrProvider(tesseractProvider));
                    ocrProviders.Add(tesseractProvider);
                    ocrProviders.Add(new native.Win10OcrProvider());
#else
                    log.Info("Using Windows 10 OCR Provider.");
                    ocrProviders.Add(new native.Win10OcrProvider());
#endif
                    SimpleServiceProvider.Current.AddService<IOcrProvider>(ocrProviders);
                    await OcrInstallationHelper.InstallHebrewOcrAsync();
                    RetentionHelper.Start();
                    _ = Task.Run(() => StartupTaskHelper.EnsureBatteryRestrictionsDisabledAsync());
                    freesnip.editor.forms.ImageEditorWindow.RequestRegionCaptureAction = () => CaptureHelper.CaptureRegion(true);
                    if (!IsNoTrayMode) 
                    { 
                        log.Info("Initializing Tray Icon and Hotkeys...");
                        await InitializeTrayIconAsync(); 
                        HotkeyManager.Start(); 
                        log.Info("Tray Icon and Hotkeys initialized successfully.");
                        if (!IniConfig.GetIniSection<CoreConfiguration>().DisableHotkeys)
                        {
                            _ = PrintScreenConflictHelper.NotifyOnBootAsync();
                        }
                    }
                    foreach (var file in options.Files)
                    {
                        await OpenImageFileInEditorAsync(file).ConfigureAwait(false);
                    }
                    if (IsNoTrayMode && desktop.Windows.Count == 0) { log.Info("No files processed and No-Tray mode active. Shutting down."); Dispatcher.UIThread.Post(() => desktop.Shutdown()); return; }
                    log.Info("Application initialization complete. Entering wait loop.");
                    await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) 
            { 
                LogHelper.GetLogger(typeof(App)).Fatal("Critical application failure during initialization.", ex);
                Dispatcher.UIThread.Post(() => { new forms.DeploymentProgressWindow("Critical Error: " + ex.Message).Show(); desktop.Shutdown(); }); 
            }
            finally
            {
                s_ipcServer?.Dispose();
                s_ipcServer = null;
                s_instanceMutex?.Dispose();
                s_instanceMutex = null;
                ForceRedTrayIcon(false);
                RetentionHelper.Stop();
                ExecutionTrace.Stop();
                IniConfig.Flush();
                foreach (var ocrProvider in ocrProviders)
                {
                    if (ocrProvider is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (ocrProvider is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }

        private void InitializePersistentConfiguration()
        {
            try
            {
                string configurationFolder = StartupTaskHelper.ConfigurationFolder;
                if (!StartupTaskHelper.IsRunningFromInstallPath()) { configurationFolder = Path.Combine(DeploymentFootprint.TempAppFolder, "Config"); }
                Directory.CreateDirectory(configurationFolder);
                IniConfigurationDeployer.EnsureDefaultsFile(configurationFolder);
                IniConfig.IniDirectory = configurationFolder;
                IniConfig.Init("FreeSnip", IniConfigurationDeployer.ConfigBaseName);
                var core = IniConfig.GetIniSection<CoreConfiguration>();
                if (string.IsNullOrWhiteSpace(core.Language)) core.Language = "en-US";
            }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"InitializePersistentConfiguration failed: {ex.Message}");
                LogHelper.GetLogger(typeof(App)).Error("Failed to initialize persistent configuration", ex);
            }
        }

        private async Task InitializeTrayIconAsync()
        {
            byte[] blueBytes = null;
            byte[] redBytes = null;

            try
            {
                using (var blueAssetLoader = AssetLoader.Open(new Uri("avares://FreeSnip/FreeSnip.ico")))
                {
                    using var ms = new MemoryStream();
                    blueAssetLoader.CopyTo(ms);
                    blueBytes = ms.ToArray();
                }

                byte[] pngBytes = TryDecodeIcoToPng(blueBytes);

                if (pngBytes != null)
                {
                    redBytes = await Task.Run(() =>
                    {
                        using var image = SixLabors.ImageSharp.Image.Load<Bgra32>(pngBytes);
                        image.Mutate(x => x.ProcessPixelRowsAsVector4(row =>
                        {
                            for (int i = 0; i < row.Length; i++)
                            {
                                float r = row[i].X;
                                float g = row[i].Y;
                                float b = row[i].Z;
                                row[i].X = Math.Max(r, Math.Max(g, b));
                                row[i].Y = g * 0.2f;
                                row[i].Z = b * 0.2f;
                            }
                        }));

                        using var ms = new MemoryStream();
                        image.Save(ms, new PngEncoder());
                        return ms.ToArray();
                    }).ConfigureAwait(true);
                }
                else
                {
                    LogHelper.GetLogger(typeof(App)).Error("Tray red-eye icon unavailable: FreeSnip.ico could not be decoded to pixels (falling back to blue-only).");
                }
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("Failed to prepare tray icons", ex);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    WindowIcon blueIcon = null;
                    WindowIcon redIcon = null;

                    if (blueBytes != null)
                    {
                        using var ms = new MemoryStream(blueBytes);
                        blueIcon = new WindowIcon(ms);
                    }

                    if (redBytes != null)
                    {
                        using var ms = new MemoryStream(redBytes);
                        redIcon = new WindowIcon(ms);
                    }

                    var icons = TrayIcon.GetIcons(this);
                    if (icons != null && icons.Count > 0)
                    {
                        _trayIcon = icons[0];
                        _blueIcon = blueIcon;
                        _redIcon = redIcon;
                        var initialIcon = _currentIconIsRed && _redIcon != null ? _redIcon : _blueIcon;
                        if (initialIcon != null) _trayIcon.Icon = initialIcon;
                        UpdateTrayMenuHotkeys();
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.GetLogger(typeof(App)).Error("Failed to create tray icon UI objects", ex);
                }
            });
        }

        public static void UpdateTrayMenuHotkeys()
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var icons = TrayIcon.GetIcons(Current);
                    if (icons == null || icons.Count == 0 || icons[0].Menu == null) return;
                    var menu = icons[0].Menu;
                    var config = IniConfig.GetIniSection<CoreConfiguration>();
                    if (config == null) return;

                    foreach (var item in menu.Items.OfType<NativeMenuItem>())
                    {
                        if (item.Header == null) continue;
                        if (item.Header.StartsWith("Capture Region"))
                            item.Header = string.IsNullOrWhiteSpace(config.RegionHotkey) ? "Capture Region" : $"Capture Region ({config.RegionHotkey})";
                        else if (item.Header.StartsWith("Repeat Last Region"))
                            item.Header = string.IsNullOrWhiteSpace(config.LastregionHotkey) ? "Repeat Last Region" : $"Repeat Last Region ({config.LastregionHotkey})";
                        else if (item.Header.StartsWith("Capture Window"))
                            item.Header = string.IsNullOrWhiteSpace(config.WindowHotkey) ? "Capture Window" : $"Capture Window ({config.WindowHotkey})";
                        else if (item.Header.StartsWith("Capture Fullscreen"))
                            item.Header = string.IsNullOrWhiteSpace(config.FullscreenHotkey) ? "Capture Fullscreen" : $"Capture Fullscreen ({config.FullscreenHotkey})";
                        else if (item.Header.StartsWith("Scroll Capture"))
                            item.Header = "Scroll Capture";
                        else if (item.Header.StartsWith("Open From Clipboard"))
                            item.Header = string.IsNullOrWhiteSpace(config.ClipboardHotkey) ? "Open From Clipboard" : $"Open From Clipboard ({config.ClipboardHotkey})";
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.GetLogger(typeof(App)).Error("Failed to update tray menu hotkeys", ex);
                }
            });
        }

        private static DateTime _lastTrayRestoreTime = DateTime.MinValue;
        private static readonly object _restoreTrayLock = new object();

        public static void RestoreTrayIcon()
        {
            lock (_restoreTrayLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastTrayRestoreTime).TotalMilliseconds < 500)
                {
                    BootstrapDebug.Log("RestoreTrayIcon: Debounced rapid tray restore request.");
                    LogHelper.GetLogger(typeof(App)).Info("[STEP:INFO] RestoreTrayIcon - Debounced rapid tray restore request (<500ms).");
                    return;
                }
                _lastTrayRestoreTime = now;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Current is App && _trayIcon != null)
                    {
                        var targetIcon = _currentIconIsRed && _redIcon != null ? _redIcon : _blueIcon;
                        if (targetIcon != null && !ReferenceEquals(_trayIcon.Icon, targetIcon))
                        {
                            _trayIcon.Icon = targetIcon;
                        }
                        if (!_trayIcon.IsVisible)
                        {
                            _trayIcon.IsVisible = true;
                        }
                        UpdateTrayMenuHotkeys();
                        BootstrapDebug.Log("RestoreTrayIcon: Tray icon visuals synchronized.");
                        LogHelper.GetLogger(typeof(App)).Info($"[STEP:SUCCESS] RestoreTrayIcon - Tray icon visuals synchronized (IsRed={_currentIconIsRed}, IsVisible={_trayIcon.IsVisible}).");
                    }
                }
                catch (Exception ex)
                {
                    BootstrapDebug.Log($"RestoreTrayIcon error: {ex.Message}");
                    LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] RestoreTrayIcon encountered error", ex);
                }
            });
        }

        private static byte[] TryDecodeIcoToPng(byte[] icoBytes)
        {
            try
            {
                if (icoBytes == null || icoBytes.Length < 6) return null;
                int count = BitConverter.ToUInt16(icoBytes, 4);

                int bestOffset = -1, bestLength = 0, bestWidth = 0, bestHeight = 0;
                long bestArea = 0;
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + i * 16;
                    if (entry + 16 > icoBytes.Length) break;
                    int width = icoBytes[entry] == 0 ? 256 : icoBytes[entry];
                    int height = icoBytes[entry + 1] == 0 ? 256 : icoBytes[entry + 1];
                    int length = BitConverter.ToInt32(icoBytes, entry + 8);
                    int offset = BitConverter.ToInt32(icoBytes, entry + 12);
                    long area = (long)width * height;
                    if (area > bestArea && offset >= 0 && length > 0 && offset + length <= icoBytes.Length)
                    {
                        bestArea = area;
                        bestOffset = offset;
                        bestLength = length;
                        bestWidth = width;
                        bestHeight = height;
                    }
                }

                if (bestOffset < 0) return null;

                byte[] subImage = new byte[bestLength];
                Array.Copy(icoBytes, bestOffset, subImage, 0, bestLength);

                if (subImage.Length > 8 && subImage[0] == 0x89 && subImage[1] == 0x50 && subImage[2] == 0x4E && subImage[3] == 0x47)
                {
                    return subImage;
                }

                return DibEntryToPng(subImage, bestWidth, bestHeight);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] DibEntryToPng(byte[] dib, int width, int height)
        {
            if (dib == null || dib.Length < 40 || width <= 0 || height <= 0) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40) return null;
            short bitsPerPixel = BitConverter.ToInt16(dib, 14);
            if (bitsPerPixel != 32) return null;

            int stride = ((width * 32 + 31) / 32) * 4;
            int pixelBytes = stride * height;
            if (headerSize + pixelBytes > dib.Length) return null;

            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(
                new ReadOnlySpan<byte>(dib, headerSize, pixelBytes), width, height);
            image.Mutate(x => x.Flip(FlipMode.Vertical));

            using var ms = new MemoryStream();
            image.Save(ms, new PngEncoder());
            return ms.ToArray();
        }

        private static readonly HashSet<string> _activeHoldSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _anonymousHoldCount = 0;
        private static volatile bool _currentIconIsRed = false;
        private static readonly object _iconLock = new object();

        internal static int ActiveRedHoldCount
        {
            get
            {
                lock (_iconLock)
                {
                    return _activeHoldSources.Count + _anonymousHoldCount;
                }
            }
        }

        public static void ForceRedTrayIcon(bool force, string reason = null)
        {
            lock (_iconLock)
            {
                bool wasRed = _activeHoldSources.Count > 0 || _anonymousHoldCount > 0;
                string source = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

                if (force)
                {
                    if (source != null)
                    {
                        _activeHoldSources.Add(source);
                    }
                    else
                    {
                        _anonymousHoldCount++;
                    }
                }
                else
                {
                    if (source != null)
                    {
                        _activeHoldSources.Remove(source);
                    }
                    else
                    {
                        _anonymousHoldCount = Math.Max(0, _anonymousHoldCount - 1);
                    }
                }

                int totalHolds = _activeHoldSources.Count + _anonymousHoldCount;
                bool isRed = totalHolds > 0;

                if (wasRed != isRed)
                {
                    LogHelper.GetLogger(typeof(App)).Info(
                        $"Tray eye -> {(isRed ? "RED (capture active)" : "BLUE (idle)")} [holds={totalHolds}]{(source == null ? "" : " " + source)}");
                }

                SetTrayIconStateInternal(isRed);
            }
        }

        public static void ClearAllTrayHolds()
        {
            lock (_iconLock)
            {
                bool wasRed = _activeHoldSources.Count > 0 || _anonymousHoldCount > 0;
                _activeHoldSources.Clear();
                _anonymousHoldCount = 0;
                if (wasRed)
                {
                    LogHelper.GetLogger(typeof(App)).Info("Tray eye -> BLUE (idle) [holds=0 - cleared all]");
                }
                SetTrayIconStateInternal(false);
            }
        }

        internal static void ResetTrayHoldStateForTesting()
        {
            ClearAllTrayHolds();
        }

        private static void SetTrayIconStateInternal(bool active)
        {
            _currentIconIsRed = active;

            void ApplyIcon()
            {
                if (_trayIcon == null) return;
                bool currentActive;
                lock (_iconLock)
                {
                    currentActive = _currentIconIsRed;
                }

                var targetIcon = currentActive && _redIcon != null ? _redIcon : _blueIcon;
                if (targetIcon == null) return;
                if (ReferenceEquals(_trayIcon.Icon, targetIcon)) return;
                _trayIcon.Icon = targetIcon;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyIcon();
            }
            else
            {
                Dispatcher.UIThread.Post(ApplyIcon, DispatcherPriority.Send);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref NativeMsg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);

        private const uint PM_REMOVE = 0x0001;

        public static void FlushPendingUiAndMessages()
        {
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    while (PeekMessage(out NativeMsg msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Warn("FlushPendingUiAndMessages encountered an error", ex);
            }
        }

        public void OnTrayIconClicked(object sender, EventArgs e)
        {
            OnCaptureRegionClick(sender, e);
        }

        public void OnCaptureRegionClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnCaptureRegionClick - Triggered region capture from tray.");
                FlushPendingUiAndMessages();
                CaptureHelper.CaptureRegion(false);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] CaptureRegion click failed", ex);
            }
        }

        public void OnCaptureLastRegionClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnCaptureLastRegionClick - Triggered repeat last region capture from tray.");
                FlushPendingUiAndMessages();
                CaptureHelper.CaptureLastRegion(false);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] CaptureLastRegion click failed", ex);
            }
        }

        public void OnCaptureWindowClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnCaptureWindowClick - Triggered active window capture from tray.");
                FlushPendingUiAndMessages();
                CaptureHelper.CaptureActiveWindow(false);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] CaptureWindow click failed", ex);
            }
        }

        private void OnCaptureFullscreenClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnCaptureFullscreenClick - Triggered fullscreen capture from tray.");
                FlushPendingUiAndMessages();
                CaptureHelper.CaptureFullscreen(false, ScreenCaptureMode.FullScreen);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] CaptureFullscreen click failed", ex);
            }
        }
        private void OnScrollCaptureClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnScrollCaptureClick - Triggered scroll capture from tray.");
                var launcher = SimpleServiceProvider.Current.GetInstance<IScrollCaptureLauncher>(true);
                _ = launcher?.StartAsync(null);
            }
            catch (Exception ex) { LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] ScrollCapture click failed", ex); }
        }
        private void OnOpenFromClipboardClick(object sender, EventArgs e)
        {
            try
            {
                LogHelper.GetLogger(typeof(App)).Info("[STEP:START] OnOpenFromClipboardClick - Triggered capture from clipboard from tray.");
                CaptureHelper.CaptureClipboard();
            }
            catch (Exception ex) { LogHelper.GetLogger(typeof(App)).Error("[STEP:FAIL] CaptureClipboard click failed", ex); }
        }
        public void OnShowHistoryClick(object sender, EventArgs e)
        {
            try 
            { 
                string tempPath = Path.Combine(Path.GetTempPath(), "FreeSnip");
                Directory.CreateDirectory(tempPath);
                Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true }); 
            } 
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("Failed to open backup history folder", ex);
            }
        }
        private static freesnip.forms.SettingsWindow _settingsWindow;

        public static void OpenOrFocusSettings()
        {
            try
            {
                var existing = _settingsWindow;
                if (existing != null)
                {
                    if (existing.WindowState == WindowState.Normal || existing.WindowState == WindowState.Minimized)
                    {
                        existing.WindowState = WindowState.Normal;
                    }
                    existing.Show();
                    existing.Activate();
                    existing.Focus();
                    var existingHwnd = existing.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (existingHwnd != IntPtr.Zero)
                    {
                        Win32WindowHelper.SetForegroundWindow(existingHwnd);
                    }
                    return;
                }

                var settingsWin = new freesnip.forms.SettingsWindow();
                _settingsWindow = settingsWin;
                settingsWin.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_settingsWindow, settingsWin)) _settingsWindow = null;
                };
                settingsWin.Show();
                settingsWin.Activate();
                settingsWin.Focus();
                var hwnd = settingsWin.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    Win32WindowHelper.SetForegroundWindow(hwnd);
                }
            }
            catch (Exception ex)
            {
                _settingsWindow = null;
                LogHelper.GetLogger(typeof(App)).Error("Failed to open or focus settings window", ex);
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            OpenOrFocusSettings();
        }

        private void StartIpcServer(CancellationToken cancellationToken)
        {
            try
            {
                s_ipcServer?.Dispose();
                s_ipcServer = new helpers.InstanceIpcServer(HandleIpcMessageAsync);
                s_ipcServer.Start();
                LogHelper.GetLogger(typeof(App)).Info("IPC server started listening on " + helpers.InstanceIpcRelay.PipeName);
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("Failed to start IPC server", ex);
            }
        }

        private async Task HandleIpcMessageAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var log = LogHelper.GetLogger(typeof(App));
            log.Info($"IPC message received: {message}");

            if (string.Equals(message, helpers.InstanceIpcRelay.MessageActivate, StringComparison.OrdinalIgnoreCase))
            {
                await Dispatcher.UIThread.InvokeAsync(HandleActivateMessage);
            }
            else if (message.StartsWith(helpers.InstanceIpcRelay.MessageOpenFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string filePath = message.Substring(helpers.InstanceIpcRelay.MessageOpenFilePrefix.Length).Trim().Trim('"');
                await OpenImageFileInEditorAsync(filePath).ConfigureAwait(false);
            }
            else
            {
                log.Warn($"Unrecognized IPC message: {message}");
            }
        }

        private void HandleActivateMessage()
        {
            try
            {
                var editor = _desktop?.Windows.OfType<freesnip.editor.forms.ImageEditorWindow>().LastOrDefault(w => w.IsVisible)
                             ?? _desktop?.Windows.OfType<freesnip.editor.forms.ImageEditorWindow>().LastOrDefault();
                if (editor != null)
                {
                    if (editor.WindowState == WindowState.Minimized)
                    {
                        editor.WindowState = WindowState.Normal;
                    }
                    editor.Show();
                    editor.Activate();
                    editor.Focus();
                    var handle = editor.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (handle != IntPtr.Zero)
                    {
                        Win32WindowHelper.SetForegroundWindow(handle);
                    }
                    LogHelper.GetLogger(typeof(App)).Info("Activated existing ImageEditorWindow via IPC.");
                }
                else
                {
                    OpenOrFocusSettings();
                    LogHelper.GetLogger(typeof(App)).Info("Opened/focused SettingsWindow via IPC.");
                }
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("Failed to handle ACTIVATE message", ex);
            }
        }

        public static async Task OpenImageFileInEditorAsync(string file)
        {
            var log = LogHelper.GetLogger(typeof(App));
            if (!File.Exists(file))
            {
                log.Warn($"File not found: {file}");
                return;
            }

            log.Info($"Opening file for editing: {file}");
            using SixLabors.ImageSharp.Image loaded = SixLabors.ImageSharp.Image.Load(file);
            SixLabors.ImageSharp.Image owned = loaded.Clone(x => { });
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                SixLabors.ImageSharp.Image imageForEditor = owned;
                freesnip.editor.forms.ImageEditorWindow editor = null;
                try
                {
                    editor = new freesnip.editor.forms.ImageEditorWindow();
                    var screen = editor.Screens.Primary ?? editor.Screens.All.FirstOrDefault();
                    int screenX = screen?.Bounds.X ?? 0;
                    int screenY = screen?.Bounds.Y ?? 0;
                    int screenW = screen?.Bounds.Width ?? imageForEditor.Width;
                    int screenH = screen?.Bounds.Height ?? imageForEditor.Height;
                    var rect = RECT.FromXYWH(
                        screenX + (screenW - imageForEditor.Width) / 2,
                        screenY + (screenH - imageForEditor.Height) / 2,
                        imageForEditor.Width,
                        imageForEditor.Height);
                    await editor.SetImageAsync(imageForEditor, rect).ConfigureAwait(true);
                    owned = null;
                    imageForEditor = null;
                    editor.Show();
                    editor.Activate();
                    editor.Focus();
                    var hwnd = editor.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (hwnd != IntPtr.Zero)
                    {
                        Win32WindowHelper.SetForegroundWindow(hwnd);
                    }

                    if (IsNoTrayMode)
                    {
                        editor.Closed += (s, ev) =>
                        {
                            if (_desktop?.Windows.Count == 0)
                            {
                                log.Info("All windows closed in No-Tray mode. Shutting down.");
                                _desktop.Shutdown();
                            }
                        };
                    }

                    log.Info($"Editor window shown for file: {file}");
                }
                catch (Exception ex)
                {
                    imageForEditor?.Dispose();
                    editor?.Close();
                    log.Error($"Failed to display editor for file: {file}", ex);
                    throw;
                }
            });
            owned?.Dispose();
        }

        public void OnViewLogsClick(object sender, EventArgs e)
        {
            try 
            { 
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSnip");
                Directory.CreateDirectory(logPath);
                Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true }); 
            } 
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(App)).Error("Failed to open application logs folder", ex);
            }
        }
        public void OnExitClick(object sender, EventArgs e)
        {
            var coordinator = SimpleServiceProvider.Current.GetInstance<IAppLifecycleCoordinator>(isOptional: true);
            if (coordinator != null)
            {
                coordinator.StopServices();
            }
            else
            {
                RetentionHelper.Stop();
                ExecutionTrace.Stop();
                HotkeyManager.Stop();
            }
            _mainAppCts.Cancel();
            s_ipcServer?.Dispose();
            s_ipcServer = null;
            s_instanceMutex?.Dispose();
            s_instanceMutex = null;
            IniConfig.Flush();
            _desktop?.Shutdown();
        }
    }
}
