using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using log4net;
using freesnip.native.foundation;
using freesnip.foundation.core;
using freesnip.foundation.Interfaces;
using freesnip.editor.forms;
using freesnip.native;
using freesnip.foundation.IniFile;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace freesnip.helpers
{
    public static class CaptureHelper
    {
        private static readonly ILog Log = LogHelper.GetLogger(typeof(CaptureHelper));

        private static readonly object LastRegionSync = new object();
        private static RECT _lastRegion = RECT.Empty;

        private static CoreConfiguration Config => IniConfig.GetIniSection<CoreConfiguration>();

        private static volatile ImageSharpImage _frozenSnapshot;
        private static RECT _frozenVirtualBounds;

        public static bool IsFrozenSnapshotReady => _frozenSnapshot != null;

        public static string LastActiveWindowTitle { get; set; }

        internal static async Task CopyCaptureToClipboardAsync(ImageSharpImage image)
        {
            try { await UiClipboard.SetImageAsync(image).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Error("Capture retained for editing; clipboard transfer failed.", ex);
                ToastHelper.ShowToast("Picture was not copied", "Your capture will open in the editor. Use Copy or Save there to keep it.");
            }
        }
        public static void CaptureRegion(bool fromHotkey)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LastActiveWindowTitle = freesnip.native.Win32WindowHelper.GetActiveWindowTitle();
            Log.Info($"[STEP:START] CaptureRegion - FromHotkey: {fromHotkey}, ActiveWindow: '{LastActiveWindowTitle}'");
            ScreenTintBypass.InvalidateCache();
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            }
            if (!forms.CaptureWindow.BeginCaptureSession())
            {
                Log.Warn($"[STEP:WARN] CaptureRegion ({sw.ElapsedMilliseconds}ms) - Capture session already active or could not begin.");
                return;
            }

            ImageSharpImage instantSnapshot = null;
            RECT instantVirtualBounds = RECT.Empty;
            if (Config.CaptureDelay <= 0)
            {
                instantVirtualBounds = GetVirtualDesktopBounds();
                instantSnapshot = NativeCapture.CaptureRegion(instantVirtualBounds, Config.CaptureMousepointer);
                Log.Info($"[STEP:SUCCESS] CaptureRegion - Instant snapshot captured ({instantVirtualBounds.Width}x{instantVirtualBounds.Height}).");
            }

            _ = Task.Run(() => CaptureRegionAsync(fromHotkey, instantSnapshot, instantVirtualBounds));
        }

        public static void ClearFrozenSnapshot()
        {
            ImageSharpImage target = _frozenSnapshot;
            if (target == null) return;

            if (System.Threading.Monitor.TryEnter(LastRegionSync))
            {
                try
                {
                    RetireFrozenSnapshotLocked(target);
                }
                finally
                {
                    System.Threading.Monitor.Exit(LastRegionSync);
                }
                return;
            }

            _ = Task.Run(() =>
            {
                lock (LastRegionSync)
                {
                    RetireFrozenSnapshotLocked(target);
                }
            });
        }

        private static void RetireFrozenSnapshotLocked(ImageSharpImage target)
        {
            if (!ReferenceEquals(_frozenSnapshot, target)) return;
            _frozenSnapshot = null;
            target.Dispose();
        }

        public static ImageSharpImage GetFrozenSnapshot(RECT target)
        {
            if (_frozenSnapshot == null) return null;

            lock (LastRegionSync)
            {
                ImageSharpImage source = _frozenSnapshot;
                if (source == null) return null;

                var cropRect = ClampCropRectangle(new Rectangle(target.Left - _frozenVirtualBounds.Left, target.Top - _frozenVirtualBounds.Top, target.Width, target.Height), source.Width, source.Height);
                if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

                return source.Clone(x => x.Crop(cropRect));
            }
        }

        private static async Task CaptureRegionAsync(bool fromHotkey, ImageSharpImage preCapturedSnapshot = null, RECT preCapturedBounds = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string sourceTitle = freesnip.native.Win32WindowHelper.GetActiveWindowTitle();
            Log.Info($"[STEP:START] CaptureRegionAsync - SourceTitle: '{sourceTitle}', FromHotkey: {fromHotkey}, HasPreSnapshot: {preCapturedSnapshot != null}");
            bool overlaysShown = false;
            try
            {
                ImageSharpImage fullSnapshot = preCapturedSnapshot;
                RECT virtualBounds = preCapturedBounds;

                if (fullSnapshot == null)
                {
                    int delay = Config.CaptureDelay > 0 ? Config.CaptureDelay : 0;
                    if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    virtualBounds = GetVirtualDesktopBounds();
                    fullSnapshot = NativeCapture.CaptureRegion(virtualBounds, Config.CaptureMousepointer);
                }
                try
                {
                    if (fullSnapshot == null)
                    {
                        sw.Stop();
                        Log.Error($"[STEP:FAIL] CaptureRegionAsync ({sw.ElapsedMilliseconds}ms) - Failed to acquire desktop capture.");
                        forms.CaptureWindow.EndCaptureSession();
                        return;
                    }

                    lock (LastRegionSync)
                    {
                        _frozenSnapshot?.Dispose();
                        _frozenSnapshot = fullSnapshot;
                        fullSnapshot = null;
                        _frozenVirtualBounds = virtualBounds;
                    }
                    Log.Info($"[STEP:SUCCESS] CaptureRegionAsync - Desktop snapshot frozen ({virtualBounds.Width}x{virtualBounds.Height}).");
                }
                finally
                {
                    fullSnapshot?.Dispose();
                }

                var screensInfo = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                    var screens = lifetime?.Windows.FirstOrDefault()?.Screens.All;
                    if (screens == null || !screens.Any())
                    {
                        var probe = new Avalonia.Controls.Window();
                        screens = probe.Screens.All;
                    }
                    return screens?.Select(s => new { s.Bounds, s.Scaling }).ToList();
                });

                if (screensInfo == null || !screensInfo.Any()) 
                {
                    sw.Stop();
                    Log.Error($"[STEP:FAIL] CaptureRegionAsync ({sw.ElapsedMilliseconds}ms) - No screens detected.");
                    forms.CaptureWindow.EndCaptureSession();
                    ClearFrozenSnapshot();
                    return;
                }
                Log.Info($"[STEP:INFO] CaptureRegionAsync - Detected {screensInfo.Count} active screens.");

                if (!IsFrozenSnapshotReady)
                {
                    sw.Stop();
                    Log.Error($"[STEP:FAIL] CaptureRegionAsync ({sw.ElapsedMilliseconds}ms) - Frozen snapshot not ready.");
                    forms.CaptureWindow.EndCaptureSession();
                    return;
                }

                bool addFrameBorders = Config.AddFrameBorders;
                var backdrops = new List<(PixelRect Bounds, Avalonia.Media.Imaging.Bitmap Bitmap)>();
                try
                {
                    backdrops = await Task.Run(() =>
                    {
                        var slices = new List<(PixelRect Bounds, Avalonia.Media.Imaging.Bitmap Bitmap)>();
                        foreach (var screen in screensInfo)
                        {
                            ImageSharpImage slice = null;
                            lock (LastRegionSync)
                            {
                                ImageSharpImage source = _frozenSnapshot;
                                if (source == null) continue;

                                var cropRect = ClampCropRectangle(
                                    new Rectangle(screen.Bounds.X - virtualBounds.Left, screen.Bounds.Y - virtualBounds.Top, screen.Bounds.Width, screen.Bounds.Height),
                                    source.Width,
                                    source.Height);
                                if (cropRect.Width <= 0 || cropRect.Height <= 0) continue;

                                slice = source.Clone(x => x.Crop(cropRect));
                            }

                            try
                            {
                                slices.Add((screen.Bounds, freesnip.editor.helpers.ImageSharpAvaloniaHelper.ToAvaloniaBitmap(slice)));
                            }
                            finally
                            {
                                slice.Dispose();
                            }
                        }
                        return slices;
                    }).ConfigureAwait(false);

                    if (backdrops.Count == 0) throw new InvalidOperationException("No monitor produced a usable capture backdrop.");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        for (int i = 0; i < backdrops.Count; i++)
                        {
                            forms.CaptureWindow window = null;
                            try
                            {
                                var slice = backdrops[i];
                                window = new forms.CaptureWindow(slice.Bounds, slice.Bitmap);
                                backdrops[i] = (slice.Bounds, null);
                                window.Show();
                            }
                            catch
                            {
                                window?.Close();
                                throw;
                            }
                        }
                    });
                    overlaysShown = true;
                    sw.Stop();
                    Log.Info($"[STEP:SUCCESS] CaptureRegionAsync ({sw.ElapsedMilliseconds}ms) - Displayed {backdrops.Count} capture overlay windows.");
                }
                finally
                {
                    foreach (var leftover in backdrops)
                    {
                        leftover.Bitmap?.Dispose();
                    }
                }
            }
            catch (Exception ex) 
            {
                sw.Stop();
                Log.Fatal($"[STEP:FAIL] CaptureRegionAsync ({sw.ElapsedMilliseconds}ms) - CaptureRegion failed.", ex);
                if (!overlaysShown) forms.CaptureWindow.EndCaptureSession();
                ClearFrozenSnapshot();
            }
        }


        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        private const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static void CaptureWindow(bool fromHotkey) { LastActiveWindowTitle = freesnip.native.Win32WindowHelper.GetActiveWindowTitle(); CaptureActiveWindow(fromHotkey); }

        public static void CaptureActiveWindow(bool fromHotkey)
        {
            App.ForceRedTrayIcon(true, "CaptureActiveWindow");
            ScreenTintBypass.InvalidateCache();
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            }
            _ = Task.Run(async () =>
            {
                ImageSharpImage fullSnapshot = null;
                ImageSharpImage owned = null;
                try
                {
                    int delay = Config.CaptureDelay > 0 ? Config.CaptureDelay : 0;
                    if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);

                    RECT virtualBounds = GetVirtualDesktopBounds();
                    fullSnapshot = NativeCapture.CaptureRegion(virtualBounds, Config.CaptureMousepointer);

                    IntPtr activeHwnd = Win32WindowHelper.GetForegroundWindow();
                    if (activeHwnd != IntPtr.Zero)
                    {
                        var sb = new System.Text.StringBuilder(256);
                        GetClassName(activeHwnd, sb, sb.Capacity);
                        string className = sb.ToString();
                        if (className == "Shell_TrayWnd" || className == "NotifyIconOverflowWindow" || className == "TrayNotifyWnd" || className == "PopupHost" || className == "WorkerW" || className == "Progman")
                        {
                            IntPtr top = GetTopWindow(IntPtr.Zero);
                            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                            int skipped = 0;
                            while (top != IntPtr.Zero && skipped < 100)
                            {
                                if (IsWindowVisible(top))
                                {
                                    var sb2 = new System.Text.StringBuilder(256);
                                    GetClassName(top, sb2, sb2.Capacity);
                                    string cls = sb2.ToString();
                                    GetWindowThreadProcessId(top, out uint pid);
                                    bool isSystem = cls == "Shell_TrayWnd" || cls == "WorkerW" || cls == "Progman" || cls == "NotifyIconOverflowWindow" || cls == "PopupHost" || cls == "EdgeUiInputTopWnd" || cls == "DummyDWMTargetWindow" || cls == "ThumbnailDeviceHelperWnd" || cls.Contains("Flyout") || cls == "Windows.UI.Core.CoreWindow" || cls == "ApplicationFrameWindow";
                                    if (pid != currentPid && !isSystem)
                                    {
                                        if (Win32WindowHelper.GetWindowRectActual(top, out RECT tr) && !tr.IsEmpty && tr.Width > 150 && tr.Height > 150) { activeHwnd = top; break; }
                                    }
                                }
                                top = GetWindow(top, GW_HWNDNEXT);
                                skipped++;
                            }
                        }
                    }

                    if (activeHwnd != IntPtr.Zero && fullSnapshot != null)
                    {
                        if (Win32WindowHelper.GetWindowRect(activeHwnd, out RECT rawRect) && !rawRect.IsEmpty)
                        {


                            if (Win32WindowHelper.GetWindowRectActual(activeHwnd, out RECT dwmRect) && !dwmRect.IsEmpty)
                            {
                                dwmRect = Win32WindowHelper.ClampRectToMonitor(activeHwnd, dwmRect);
                                if (!dwmRect.IsEmpty) rawRect = dwmRect;
                            }

                            var cropRect = ClampCropRectangle(new Rectangle(rawRect.Left - virtualBounds.Left, rawRect.Top - virtualBounds.Top, rawRect.Width, rawRect.Height), fullSnapshot.Width, fullSnapshot.Height);
                            if (cropRect.Width > 0 && cropRect.Height > 0)
                            {
                                owned = fullSnapshot.Clone(x => x.Crop(cropRect));

                                if (Config.KeepBackup)
                                {
                                    try
                                    {
                                        string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                                        Directory.CreateDirectory(tempDir);
                                        string ext = Config.OutputFileAllowPng ? "png" : "jpg";
                                        string fileName = $"Raw_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.{ext}";
                                        if (Config.OutputFileAllowPng)
                                        {
                                            owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                                        }
                                        else
                                        {
                                            owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = Config.OutputFileJpegQuality });
                                        }
                                        Log.Info($"[STEP:SUCCESS] CaptureActiveWindow - Raw backup saved to '{Path.Combine(tempDir, fileName)}'.");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Error("[TEMP_SAVE_FAILURE] Failed to save raw capture.", ex);
                                    }
                                }

                                RememberRegion(rawRect);
                                await CaptureHelper.CopyCaptureToClipboardAsync(owned).ConfigureAwait(false);
                                ImageSharpImage imageForEditor = owned;
                                await Dispatcher.UIThread.InvokeAsync(() => ShowEditorForOwnedImageAsync(imageForEditor, rawRect, "region"));
                                owned = null;
                                Log.Info($"[STEP:SUCCESS] CaptureActiveWindow - Window capture completed ({rawRect.Width}x{rawRect.Height}).");
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Fatal("[STEP:FAIL] CaptureActiveWindow failed.", ex); }
                finally
                {
                    owned?.Dispose();
                    fullSnapshot?.Dispose();
                    App.ForceRedTrayIcon(false, "CaptureActiveWindow");
                    App.ClearAllTrayHolds();
                }
            });
        }

        public static void CaptureFullscreen(bool fromHotkey, ScreenCaptureMode mode)
        {
            App.ForceRedTrayIcon(true, "CaptureFullscreen");
            LastActiveWindowTitle = freesnip.native.Win32WindowHelper.GetActiveWindowTitle();
            ScreenTintBypass.InvalidateCache();
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            }
            _ = Task.Run(async () =>
            {
                ImageSharpImage owned = null;
                try
                {
                    int delay = Config.CaptureDelay > 0 ? Config.CaptureDelay : 0;
                    if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);

                    ScreenCaptureMode effectiveMode = mode != ScreenCaptureMode.Auto ? mode : Config.ScreenCaptureMode;
                    RECT targetBounds = GetBoundsForMode(effectiveMode);
                    using var fullSnapshot = NativeCapture.CaptureRegion(targetBounds, Config.CaptureMousepointer);

                    if (fullSnapshot != null)
                    {
                        owned = fullSnapshot.Clone(x => { });
                        if (Config.KeepBackup)
                        {
                            try
                            {
                                string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                                Directory.CreateDirectory(tempDir);
                                string ext = Config.OutputFileAllowPng ? "png" : "jpg";
                                string fileName = $"Raw_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.{ext}";
                                if (Config.OutputFileAllowPng)
                                {
                                    owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                                }
                                else
                                {
                                    owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = Config.OutputFileJpegQuality });
                                }
                                Log.Info($"[STEP:SUCCESS] CaptureFullscreen - Raw backup saved to '{Path.Combine(tempDir, fileName)}'.");
                            }
                            catch (Exception ex)
                            {
                                Log.Error("[TEMP_SAVE_FAILURE] Failed to save raw capture.", ex);
                            }
                        }

                        await CaptureHelper.CopyCaptureToClipboardAsync(owned).ConfigureAwait(false);
                        ImageSharpImage imageForEditor = owned;
                        await Dispatcher.UIThread.InvokeAsync(() => ShowEditorForOwnedImageAsync(imageForEditor, targetBounds, "region"));
                        owned = null;
                        Log.Info($"[STEP:SUCCESS] CaptureFullscreen - Fullscreen capture displayed in editor ({targetBounds.Width}x{targetBounds.Height}).");
                    }
                }
                catch (Exception ex) { Log.Fatal("[STEP:FAIL] CaptureFullscreen failed.", ex); }
                finally
                {
                    owned?.Dispose();
                    App.ForceRedTrayIcon(false, "CaptureFullscreen");
                    App.ClearAllTrayHolds();
                }
            });
        }

        public static RECT GetBoundsForMode(ScreenCaptureMode mode)
        {
            if (mode == ScreenCaptureMode.FullScreen)
            {
                return RECT.FromXYWH(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
            }
            return GetVirtualDesktopBounds();
        }

        public static void CaptureClipboard()
        {
            LastActiveWindowTitle = freesnip.native.Win32WindowHelper.GetActiveWindowTitle();
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    ImageSharpImage image = await UiClipboard.GetImageAsync();
                    if (image != null)
                    {
                        await ShowEditorForOwnedImageAsync(image, RECT.Empty, "clipboard");
                    }
                    else
                    {
                        App.ClearAllTrayHolds();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CaptureClipboard failed", ex);
                    App.ClearAllTrayHolds();
                }
            });
        }

        public static void RememberRegion(RECT region)
        {
            if (!region.IsEmpty && region.Width > 0 && region.Height > 0)
            {
                lock (LastRegionSync) _lastRegion = region;
            }
        }

        public static void CaptureLastRegion(bool fromHotkey)
        {
            App.ForceRedTrayIcon(true, "CaptureLastRegion");
            try
            {
                ScreenTintBypass.InvalidateCache();
                if (Dispatcher.UIThread.CheckAccess())
                {
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                }
                RECT lastRegion;
                lock (LastRegionSync) lastRegion = _lastRegion;
                if (lastRegion.IsEmpty || lastRegion.Width <= 0 || lastRegion.Height <= 0)
                {
                    App.ForceRedTrayIcon(false, "CaptureLastRegion");
                    App.ClearAllTrayHolds();
                    return;
                }
                OpenEditorForRegionAsync(lastRegion);
            }
            catch (Exception ex)
            {
                App.ForceRedTrayIcon(false, "CaptureLastRegion");
                App.ClearAllTrayHolds();
                Log.Fatal("CaptureLastRegion failed.", ex);
            }
        }

        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCXVirtualScreen = 78;
        private const int SmCYVirtualScreen = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public static RECT GetVirtualDesktopBounds()
        {
            return RECT.FromXYWH(
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCXVirtualScreen),
                GetSystemMetrics(SmCYVirtualScreen));
        }

        private static void OpenEditorForRegionAsync(RECT region)
        {
            ImageSharpImage owned = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    using (ImageSharpImage captured = NativeCapture.CaptureRegion(region, Config.CaptureMousepointer))
                    {
                        if (captured == null)
                        {
                            App.ClearAllTrayHolds();
                            return;
                        }
                        owned = captured.Clone(x => { });

                        if (Config.KeepBackup)
                        {
                            try
                            {
                                string tempDir = Path.Combine(Path.GetTempPath(), "FreeSnip");
                                Directory.CreateDirectory(tempDir);
                                string ext = Config.OutputFileAllowPng ? "png" : "jpg";
                                string fileName = $"Raw_{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}.{ext}";
                                if (Config.OutputFileAllowPng)
                                {
                                    owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                                }
                                else
                                {
                                    owned.Save(Path.Combine(tempDir, fileName), new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = Config.OutputFileJpegQuality });
                                }
                                Log.Info($"[STEP:SUCCESS] OpenEditorForRegionAsync - Raw backup saved to '{Path.Combine(tempDir, fileName)}'.");
                            }
                            catch (Exception ex)
                            {
                                Log.Error("[TEMP_SAVE_FAILURE] Failed to save raw capture.", ex);
                            }
                        }
                    }

                    if (owned == null)
                    {
                        App.ClearAllTrayHolds();
                        return;
                    }
                    RememberRegion(region);
                    await CaptureHelper.CopyCaptureToClipboardAsync(owned).ConfigureAwait(false);
                    ImageSharpImage imageForEditor = owned;
                    await Dispatcher.UIThread.InvokeAsync(() => ShowEditorForOwnedImageAsync(imageForEditor, region, "region"));
                    owned = null;
                    Log.Info($"[STEP:SUCCESS] OpenEditorForRegionAsync - Region capture displayed in editor ({region.Width}x{region.Height}).");
                }
                catch (Exception ex)
                {
                    Log.Fatal("[STEP:FAIL] OpenEditorForRegion failed.", ex);
                }
                finally
                {
                    owned?.Dispose();
                    App.ForceRedTrayIcon(false, "CaptureLastRegion");
                    App.ClearAllTrayHolds();
                }
            });
        }

        public static void OpenEditorForOwnedImage(ImageSharpImage image, RECT region)
        {
            _ = ShowEditorForOwnedImageAsync(image, region, "scroll");
        }

        private static async Task ShowEditorForOwnedImageAsync(ImageSharpImage image, RECT region, string context)
        {
            ImageEditorWindow editor = null;
            try
            {
                editor = new ImageEditorWindow();
                await editor.SetImageAsync(image, region, LastActiveWindowTitle).ConfigureAwait(true);
                editor.Show();
                App.ClearAllTrayHolds();
            }
            catch (Exception ex)
            {
                App.ClearAllTrayHolds();
                image?.Dispose();
                editor?.Close();
                Log.Fatal("ShowEditorForOwnedImage failed.", ex);
            }
        }

        private static Rectangle ClampCropRectangle(Rectangle rectangle, int imageWidth, int imageHeight)
        {
            int x = Math.Max(0, rectangle.X);
            int y = Math.Max(0, rectangle.Y);
            int right = Math.Min(imageWidth, rectangle.Right);
            int bottom = Math.Min(imageHeight, rectangle.Bottom);
            return new Rectangle(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }
    }
}
