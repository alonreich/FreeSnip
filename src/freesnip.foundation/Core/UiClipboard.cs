using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace freesnip.foundation.core
{
    public static class UiClipboard
    {


        private static readonly object _textHandlerLock = new object();
        private static readonly System.Collections.Generic.List<(object Owner, Func<string, Task> SetTextAsync)> _textHandlers
            = new System.Collections.Generic.List<(object, Func<string, Task>)>();
        private static Func<Avalonia.Input.Platform.IClipboard> _getClipboard;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint style, string className, string title, uint windowStyle,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
            public uint lPrivate;
        }

        [DllImport("user32.dll")] private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage([In] ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        public const uint CF_UNICODETEXT_FORMAT = 13;
        public const uint CF_DIB_FORMAT = 8;
        public const uint CF_HDROP_FORMAT = 15;
        public const uint CF_DIBV5_FORMAT = 17;

        private const uint CF_UNICODETEXT = CF_UNICODETEXT_FORMAT;
        private const uint CF_DIB = CF_DIB_FORMAT;
        private const uint CF_HDROP = CF_HDROP_FORMAT;
        private const uint CF_DIBV5 = CF_DIBV5_FORMAT;
        private const uint GHND = 0x0042;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private static readonly object _ownerLock = new object();
        private static readonly object _nativeClipboardLock = new object();
        private static IntPtr _clipboardOwnerHwnd = IntPtr.Zero;
        private const int DefaultClipboardHistoryPromotionDelayMs = 0;
        public const string FreeSnipEditorImageFormat = "FreeSnip.ImageEditorSource";
        public const string FreeSnipAnnotationFormat = "FreeSnip.AnnotationObject";
        private static readonly byte[] FreeSnipEditorImageBytes = { 1 };
        private static readonly byte[] FreeSnipAnnotationBytes = { 2 };

        private static int _nonWindowsClipboardSequence;

        public static uint GetCurrentSequenceNumber()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetClipboardSequenceNumber();
            }
            return (uint)Interlocked.Increment(ref _nonWindowsClipboardSequence);
        }

        public static bool ProbeClipboardFormat(uint format, int maxWaitMs = 100)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds <= maxWaitMs)
            {
                if (IsClipboardFormatAvailable(format))
                {
                    return true;
                }
                Thread.Sleep(2);
            }
            return IsClipboardFormatAvailable(format);
        }

        public static bool HasNativeImage()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            try
            {
                if (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5))
                {
                    return true;
                }

                uint pngFormat = RegisterClipboardFormat("PNG");
                if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat)) return true;

                uint imagePngFormat = RegisterClipboardFormat("image/png");
                if (imagePngFormat != 0 && IsClipboardFormatAvailable(imagePngFormat)) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void Register(Func<string, Task> setTextAsync) => Register(null, setTextAsync);

        public static void Register(object owner, Func<string, Task> setTextAsync)
        {
            if (setTextAsync == null) return;
            lock (_textHandlerLock)
            {
                if (owner == null)
                {

                    _textHandlers.Clear();
                    _textHandlers.Add((null, setTextAsync));
                    return;
                }

                for (int i = 0; i < _textHandlers.Count; i++)
                {
                    if (ReferenceEquals(_textHandlers[i].Owner, owner))
                    {
                        _textHandlers[i] = (owner, setTextAsync);
                        return;
                    }
                }

                _textHandlers.Add((owner, setTextAsync));
            }
        }

        public static void Unregister(object owner)
        {
            if (owner == null) return;
            lock (_textHandlerLock)
            {
                _textHandlers.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
            }
        }

        public static void RegisterGetter(Func<Avalonia.Input.Platform.IClipboard> getClipboard) => _getClipboard = getClipboard;
        public static Avalonia.Input.Platform.IClipboard GetClipboard() => _getClipboard?.Invoke();

        public static async Task SetTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Info($"[STEP:START] SetTextAsync - Length: {text.Length}");

            Func<string, Task> handler;
            lock (_textHandlerLock)
            {
                handler = _textHandlers.Count > 0 ? _textHandlers[_textHandlers.Count - 1].SetTextAsync : null;
            }

            if (handler != null)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await handler(text);
                    });
                    log.Info($"[STEP:SUCCESS] SetTextAsync ({sw.ElapsedMilliseconds}ms) - Handled via registered window handler.");
                    return;
                }
                catch (Exception ex)
                {
                    log.Warn("Window clipboard handler failed; falling back to native.", ex);
                }
            }

            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await clipboard.SetTextAsync(text);
                    });
                    log.Info($"[STEP:SUCCESS] SetTextAsync ({sw.ElapsedMilliseconds}ms) - Set via Avalonia IClipboard.");
                    return;
                }
                catch { }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (SetWin32ClipboardText(text))
                {
                    log.Info($"[STEP:SUCCESS] SetTextAsync ({sw.ElapsedMilliseconds}ms) - Set via Win32 SetWin32ClipboardText.");
                    return;
                }
            }

            log.Error($"[STEP:FAIL] SetTextAsync ({sw.ElapsedMilliseconds}ms) - Text could not be copied to clipboard.");
            throw new InvalidOperationException("Text could not be copied to the clipboard. Please try again.");
        }

        public static async Task<bool> SetPlainTextOnlyAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            log.Info($"[STEP:START] SetPlainTextOnlyAsync - Length: {text.Length}, Text: {text}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool nativeSuccess = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr hText = AllocateGlobalString(text);
                if (hText != IntPtr.Zero)
                {
                    bool textSet = false;
                    try
                    {
                        nativeSuccess = TryWriteNativeClipboard(() =>
                        {
                            if (SetClipboardData(CF_UNICODETEXT, hText) != IntPtr.Zero)
                            {
                                textSet = true;
                                return true;
                            }
                            return false;
                        }, maxRetries: 25);
                    }
                    finally
                    {
                        if (!textSet && hText != IntPtr.Zero) GlobalFree(hText);
                    }
                }
            }

            Func<string, Task> handler = null;
            lock (_textHandlerLock)
            {
                if (_textHandlers.Count > 0)
                {
                    handler = _textHandlers[_textHandlers.Count - 1].SetTextAsync;
                }
            }
            if (handler != null)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await handler(text);
                    });
                }
                catch (Exception ex)
                {
                    log.Warn("Registered clipboard handler failed in SetPlainTextOnlyAsync", ex);
                }
            }

            try
            {
                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await clipboard.SetTextAsync(text);
                    });
                }
            }
            catch (Exception ex)
            {
                log.Warn("Avalonia clipboard text synchronization failed", ex);
            }

            sw.Stop();
            bool probeOk = ProbeClipboardFormat(CF_UNICODETEXT_FORMAT, 150);
            bool overallSuccess = nativeSuccess || probeOk;
            log.Info($"[STEP:{(overallSuccess ? "SUCCESS" : "FAIL")}] SetPlainTextOnlyAsync ({sw.ElapsedMilliseconds}ms) - NativeSet={nativeSuccess}, ProbeOk={probeOk}");
            return overallSuccess;
        }

        private static bool SetWin32ClipboardText(string text)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrEmpty(text)) return false;
            IntPtr hText = AllocateGlobalString(text);
            if (hText == IntPtr.Zero) return false;

            bool textSet = false;
            try
            {
                return TryWriteNativeClipboard(() =>
                {
                    if (SetClipboardData(CF_UNICODETEXT, hText) != IntPtr.Zero)
                    {
                        textSet = true;
                        return true;
                    }
                    return false;
                });
            }
            finally
            {
                if (!textSet && hText != IntPtr.Zero) GlobalFree(hText);
            }
        }

        private static readonly ManualResetEventSlim _ownerWindowReady = new(false);

        public static IntPtr GetClipboardOwnerHwnd()
        {
            if (_clipboardOwnerHwnd != IntPtr.Zero && IsWindow(_clipboardOwnerHwnd))
            {
                return _clipboardOwnerHwnd;
            }

            lock (_ownerLock)
            {
                if (_clipboardOwnerHwnd != IntPtr.Zero && IsWindow(_clipboardOwnerHwnd))
                {
                    return _clipboardOwnerHwnd;
                }

                _ownerWindowReady.Reset();
                var pumpThread = new Thread(ClipboardPumpThreadProc)
                {
                    IsBackground = true,
                    Name = "FreeSnip Clipboard Pump"
                };
                pumpThread.SetApartmentState(ApartmentState.STA);
                pumpThread.Start();

                if (!_ownerWindowReady.Wait(3000))
                {
                    _clipboardOwnerHwnd = CreateClipboardOwnerWindow();
                }

                return _clipboardOwnerHwnd;
            }
        }

        private static void ClipboardPumpThreadProc()
        {
            _clipboardOwnerHwnd = CreateClipboardOwnerWindow();
            _ownerWindowReady.Set();

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private static IntPtr CreateClipboardOwnerWindow()
        {
            return CreateWindowExW(0, "STATIC", "FreeSnip Clipboard", 0,
                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        public static IntPtr GetCurrentClipboardOwner() => GetClipboardOwner();
        public static bool IsWindowHandle(IntPtr hWnd) => IsWindow(hWnd);

        private static bool TryWriteNativeClipboard(Func<bool> write, int maxRetries = 25)
        {
            lock (_nativeClipboardLock)
            {
                IntPtr owner = GetClipboardOwnerHwnd();
                if (owner == IntPtr.Zero) return false;

                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    if (OpenClipboard(owner))
                    {
                        try
                        {
                            if (!EmptyClipboard())
                            {
                                continue;
                            }
                            return write();
                        }
                        finally
                        {
                            CloseClipboard();
                        }
                    }

                    if (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(attempt < 3 ? 1 : (attempt < 10 ? 2 : 5));
                    }
                }

                return false;
            }
        }

        [Obsolete("Deprecated. UiClipboard uses atomic Win32 multi-format clipboard batching with zero artificial delay.")]
        private static int GetClipboardHistoryPromotionDelayMs()
        {
            try
            {
                var core = freesnip.foundation.IniFile.IniConfig.GetIniSection<CoreConfiguration>();
                if (core != null && core.ClipboardHistoryPromotionDelayMs >= 0) return core.ClipboardHistoryPromotionDelayMs;
            }
            catch { }
            return DefaultClipboardHistoryPromotionDelayMs;
        }

        public static async Task SetFilePathThenImageAsync(string filePath, Image image, bool markFreeSnipEditorImage = false)
        {
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Info($"[STEP:START] SetFilePathThenImageAsync - FilePath: '{filePath}', HasImage: {image != null}");

            if (image == null && string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (image == null)
            {
                await SetTextAsync(Path.GetFullPath(filePath)).ConfigureAwait(false);
                log.Info($"[STEP:SUCCESS] SetFilePathThenImageAsync ({sw.ElapsedMilliseconds}ms) - Delegated to SetTextAsync.");
                return;
            }

            string fullPath = !string.IsNullOrWhiteSpace(filePath) ? Path.GetFullPath(filePath) : null;
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                await SetImageAsync(image, markFreeSnipEditorImage).ConfigureAwait(false);
                log.Info($"[STEP:SUCCESS] SetFilePathThenImageAsync ({sw.ElapsedMilliseconds}ms) - Delegated to SetImageAsync.");
                return;
            }

            var encoded = await Task.Run(() => EncodeClipboardImage(image)).ConfigureAwait(false);
            byte[] dibBytes = encoded.DibBytes;
            byte[] bmpFullBytes = encoded.BmpFullBytes;
            byte[] pngBytes = encoded.PngBytes;
            byte[] dibV5Bytes = await Task.Run(() => HasAlphaChannel(image) ? CreateDibV5(image) : null).ConfigureAwait(false);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && dibBytes != null)
            {
                uint pngFormat = RegisterClipboardFormat("PNG");
                uint imagePngFormat = RegisterClipboardFormat("image/png");
                uint freeSnipFormat = markFreeSnipEditorImage ? RegisterClipboardFormat(FreeSnipEditorImageFormat) : 0;

                IntPtr hText = AllocateGlobalString(fullPath);
                IntPtr hDib = AllocateGlobalBytes(dibBytes);
                IntPtr hDibV5 = AllocateGlobalBytes(dibV5Bytes);
                IntPtr hPng = AllocateGlobalBytes(pngBytes);
                IntPtr hImagePng = AllocateGlobalBytes(pngBytes);
                IntPtr hDropFiles = AllocateGlobalDropFiles(fullPath);
                IntPtr hFreeSnip = markFreeSnipEditorImage ? AllocateGlobalBytes(FreeSnipEditorImageBytes) : IntPtr.Zero;

                bool textSet = false;
                bool dibSet = false;
                bool dibV5Set = false;
                bool pngSet = false;
                bool imagePngSet = false;
                bool dropFilesSet = false;
                bool freeSnipSet = false;

                try
                {
                    bool success = TryWriteNativeClipboard(() =>
                    {
                        if (hDib != IntPtr.Zero && SetClipboardData(CF_DIB, hDib) != IntPtr.Zero)
                            dibSet = true;

                        if (hDibV5 != IntPtr.Zero && SetClipboardData(CF_DIBV5, hDibV5) != IntPtr.Zero)
                            dibV5Set = true;

                        if (pngFormat != 0 && hPng != IntPtr.Zero && SetClipboardData(pngFormat, hPng) != IntPtr.Zero)
                            pngSet = true;

                        if (imagePngFormat != 0 && hImagePng != IntPtr.Zero && SetClipboardData(imagePngFormat, hImagePng) != IntPtr.Zero)
                            imagePngSet = true;

                        if (hDropFiles != IntPtr.Zero && SetClipboardData(CF_HDROP, hDropFiles) != IntPtr.Zero)
                            dropFilesSet = true;

                        if (hText != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, hText) != IntPtr.Zero)
                            textSet = true;

                        if (freeSnipFormat != 0 && hFreeSnip != IntPtr.Zero && SetClipboardData(freeSnipFormat, hFreeSnip) != IntPtr.Zero)
                            freeSnipSet = true;

                        return dibSet || textSet;
                    });

                    if (success)
                    {
                        log.Info($"[STEP:SUCCESS] SetFilePathThenImageAsync ({sw.ElapsedMilliseconds}ms) - Native batch formats written.");
                        return;
                    }
                }
                finally
                {
                    if (!textSet && hText != IntPtr.Zero) GlobalFree(hText);
                    if (!dibSet && hDib != IntPtr.Zero) GlobalFree(hDib);
                    if (!dibV5Set && hDibV5 != IntPtr.Zero) GlobalFree(hDibV5);
                    if (!pngSet && hPng != IntPtr.Zero) GlobalFree(hPng);
                    if (!imagePngSet && hImagePng != IntPtr.Zero) GlobalFree(hImagePng);
                    if (!dropFilesSet && hDropFiles != IntPtr.Zero) GlobalFree(hDropFiles);
                    if (!freeSnipSet && hFreeSnip != IntPtr.Zero) GlobalFree(hFreeSnip);
                }
            }

            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                var dataObject = new DataObject();
                dataObject.Set(DataFormats.Text, fullPath);
                dataObject.Set("PNG", pngBytes);
                dataObject.Set("image/png", pngBytes);
                dataObject.Set("Bitmap", bmpFullBytes);
                if (markFreeSnipEditorImage)
                {
                    dataObject.Set(FreeSnipEditorImageFormat, FreeSnipEditorImageBytes);
                }
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await clipboard.SetDataObjectAsync(dataObject);
                });
                log.Info($"[STEP:SUCCESS] SetFilePathThenImageAsync ({sw.ElapsedMilliseconds}ms) - Avalonia DataObject batch set.");
                return;
            }

            await SetImageAsync(image, markFreeSnipEditorImage).ConfigureAwait(false);
            await SetTextAsync(fullPath).ConfigureAwait(false);
            log.Info($"[STEP:SUCCESS] SetFilePathThenImageAsync ({sw.ElapsedMilliseconds}ms) - Sequential image and text set.");
        }

        public static Task SetAtomicMultiFormatAsync(Image image, string filePath = null, bool markFreeSnipEditorImage = false)
        {
            return SetFilePathThenImageAsync(filePath, image, markFreeSnipEditorImage);
        }

        public static async Task<bool> HasFreeSnipEditorImageAsync()
        {
            try
            {
                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    bool hasAvaloniaFormat = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var formats = await clipboard.GetFormatsAsync();
                        return formats.Contains(FreeSnipEditorImageFormat);
                    });
                    if (hasAvaloniaFormat)
                    {
                        return true;
                    }
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    uint format1 = RegisterClipboardFormat(FreeSnipEditorImageFormat);
                    if (format1 != 0 && IsClipboardFormatAvailable(format1)) return true;
                }
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(UiClipboard)).Error("Failed to inspect FreeSnip clipboard marker", ex);
            }

            return false;
        }

        private static bool _inMemoryAnnotationMarker;

        private static bool CanAccessNativeClipboard()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            if (OpenClipboard(IntPtr.Zero))
            {
                CloseClipboard();
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            return err != 5;
        }

        public static void SetInMemoryAnnotationMarker(bool value)
        {
            _inMemoryAnnotationMarker = value;
        }

        public static void SetAnnotationMarker(string textContent = null)
        {
            _inMemoryAnnotationMarker = true;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Info($"[STEP:START] SetAnnotationMarker - HasText: {!string.IsNullOrEmpty(textContent)}");
            try
            {
                uint format = RegisterClipboardFormat(FreeSnipAnnotationFormat);
                if (format == 0) return;

                byte[] markerBytes = FreeSnipAnnotationBytes;
                IntPtr hMarker = AllocateGlobalBytes(markerBytes);
                IntPtr hText = !string.IsNullOrEmpty(textContent) ? AllocateGlobalString(textContent) : IntPtr.Zero;

                bool markerSet = false;
                bool textSet = false;

                try
                {
                    bool success = TryWriteNativeClipboard(() =>
                    {
                        if (hMarker != IntPtr.Zero && SetClipboardData(format, hMarker) != IntPtr.Zero)
                            markerSet = true;
                        if (hText != IntPtr.Zero && SetClipboardData(CF_UNICODETEXT, hText) != IntPtr.Zero)
                            textSet = true;
                        return markerSet;
                    }, maxRetries: 5);
                    log.Info($"[STEP:{(success ? "SUCCESS" : "WARN")}] SetAnnotationMarker ({sw.ElapsedMilliseconds}ms) - MarkerSet={markerSet}, TextSet={textSet}");
                }
                finally
                {
                    if (!markerSet && hMarker != IntPtr.Zero) GlobalFree(hMarker);
                    if (!textSet && hText != IntPtr.Zero) GlobalFree(hText);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[STEP:FAIL] SetAnnotationMarker ({sw.ElapsedMilliseconds}ms) - Failed to set annotation clipboard marker", ex);
            }
        }

        public static bool HasAnnotationMarker()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _inMemoryAnnotationMarker;
            try
            {
                uint format = RegisterClipboardFormat(FreeSnipAnnotationFormat);
                if (format != 0 && IsClipboardFormatAvailable(format)) return true;

                if (!CanAccessNativeClipboard())
                {
                    return _inMemoryAnnotationMarker;
                }
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.GetLogger(typeof(UiClipboard)).Error("Failed to inspect annotation clipboard marker", ex);
                return _inMemoryAnnotationMarker;
            }
        }

        public static async Task<Image> GetImageAsync()
        {
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Info("[STEP:START] GetImageAsync - Inspecting clipboard for image data.");
            try
            {
                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    var avaloniaImage = await TryGetAvaloniaClipboardImageAsync(clipboard).ConfigureAwait(false);
                    if (avaloniaImage != null)
                    {
                        log.Info($"[STEP:SUCCESS] GetImageAsync ({sw.ElapsedMilliseconds}ms) - Retrieved image via Avalonia ({avaloniaImage.Width}x{avaloniaImage.Height}).");
                        return avaloniaImage;
                    }
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var win32Image = await Task.Run(() => TryGetWin32ClipboardImage()).ConfigureAwait(false);
                    if (win32Image != null)
                    {
                        log.Info($"[STEP:SUCCESS] GetImageAsync ({sw.ElapsedMilliseconds}ms) - Retrieved image via Win32 ({win32Image.Width}x{win32Image.Height}).");
                        return win32Image;
                    }
                }
                log.Info($"[STEP:INFO] GetImageAsync ({sw.ElapsedMilliseconds}ms) - No image found on clipboard.");
            }
            catch (Exception ex)
            {
                log.Error($"[STEP:FAIL] GetImageAsync ({sw.ElapsedMilliseconds}ms) - Failed to get image from clipboard", ex);
            }
            return null;
        }

        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

        private static async Task<Image> TryGetAvaloniaClipboardImageAsync(Avalonia.Input.Platform.IClipboard clipboard)
        {
            string[] candidateFormats = null;
            object[] candidateData = null;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var formats = await clipboard.GetFormatsAsync();
                var fmtList = new System.Collections.Generic.List<string>();
                var dataList = new System.Collections.Generic.List<object>();
                foreach (string format in new[] { "PNG", "image/png", "JPEG", "JPG", "JFIF", "image/jpeg", "image/jpg", "Bitmap", "CF_DIB", "DeviceIndependentBitmap" })
                {
                    if (!formats.Contains(format))
                    {
                        continue;
                    }

                    var data = await clipboard.GetDataAsync(format);
                    if (data != null)
                    {
                        fmtList.Add(format);
                        dataList.Add(data);
                    }
                }
                candidateFormats = fmtList.ToArray();
                candidateData = dataList.ToArray();
            });

            if (candidateFormats == null || candidateFormats.Length == 0)
            {
                return null;
            }

            string[] formatsCopy = candidateFormats;
            object[] dataCopy = candidateData;

            return await Task.Run(() =>
            {
                for (int i = 0; i < formatsCopy.Length; i++)
                {
                    var image = TryLoadImageFromData(dataCopy[i]);
                    if (image == null && dataCopy[i] is byte[] dibBytes && (formatsCopy[i].Contains("DIB", StringComparison.OrdinalIgnoreCase) || formatsCopy[i].Contains("DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            image = LoadDibImage(dibBytes);
                        }
                        catch
                        {
                        }
                    }

                    if (image != null)
                    {
                        return image;
                    }
                }

                return null;
            }).ConfigureAwait(false);
        }

        private static Image TryLoadImageFromData(object data)
        {
            try
            {
                if (data is byte[] bytes && bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    return Image.Load(ms);
                }

                if (data is Stream stream)
                {
                    using var ms = new MemoryStream();
                    if (stream.CanSeek)
                    {
                        stream.Position = 0;
                    }

                    stream.CopyTo(ms);
                    ms.Position = 0;
                    return Image.Load(ms);
                }

                if (data is Avalonia.Media.Imaging.Bitmap bitmap)
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms);
                    ms.Position = 0;
                    return Image.Load(ms);
                }
            }
            catch
            {
            }

            return null;
        }

        private static Image TryGetWin32ClipboardImage()
        {
            lock (_nativeClipboardLock)
            {
                const int maxRetries = 10;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        try
                        {
                            foreach (string formatName in new[] { "PNG", "image/png", "JPEG", "JPG", "JFIF", "image/jpeg", "image/jpg" })
                            {
                                uint format = RegisterClipboardFormat(formatName);
                                if (format == 0 || !IsClipboardFormatAvailable(format))
                                {
                                    continue;
                                }

                                byte[] bytes = CopyClipboardGlobalBytes(format);
                                if (bytes == null || bytes.Length == 0)
                                {
                                    continue;
                                }

                                try
                                {
                                    using var ms = new MemoryStream(bytes);
                                    return Image.Load(ms);
                                }
                                catch
                                {
                                }
                            }

                            if (IsClipboardFormatAvailable(CF_DIBV5))
                            {
                                byte[] dibV5Data = CopyClipboardGlobalBytes(CF_DIBV5);
                                if (dibV5Data != null && dibV5Data.Length > 0)
                                {
                                    try
                                    {
                                        var v5Image = LoadDibImage(dibV5Data);
                                        if (v5Image != null) return v5Image;
                                    }
                                    catch
                                    {
                                    }
                                }
                            }

                            if (IsClipboardFormatAvailable(CF_DIB))
                            {
                                byte[] dibData = CopyClipboardGlobalBytes(CF_DIB);
                                if (dibData != null && dibData.Length > 0)
                                {
                                    return LoadDibImage(dibData);
                                }
                            }

                            return null;
                        }
                        finally
                        {
                            CloseClipboard();
                        }
                    }

                    if (attempt < maxRetries - 1)
                    {
                        Thread.Sleep(attempt == 0 ? 1 : 5);
                    }
                }

                return null;
            }
        }

        private static byte[] CopyClipboardGlobalBytes(uint format)
        {
            IntPtr hGlobal = GetClipboardData(format);
            if (hGlobal == IntPtr.Zero)
            {
                return null;
            }

            IntPtr lpGlobal = GlobalLock(hGlobal);
            if (lpGlobal == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                ulong size = GlobalSize(hGlobal).ToUInt64();
                if (size == 0 || size > int.MaxValue)
                {
                    return null;
                }

                byte[] data = new byte[(int)size];
                Marshal.Copy(lpGlobal, data, 0, data.Length);
                return data;
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
        }

        private static Image LoadDibImage(byte[] dibData)
        {
            int pixelOffset = CalculateDibPixelOffset(dibData);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)0x4D42);
            bw.Write((uint)(14 + dibData.Length));
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((uint)(14 + pixelOffset));
            bw.Write(dibData);
            ms.Position = 0;
            return Image.Load(ms);
        }

        private static int CalculateDibPixelOffset(byte[] dibData)
        {
            if (dibData == null || dibData.Length < 4)
            {
                return 0;
            }

            int headerSize = BitConverter.ToInt32(dibData, 0);
            if (headerSize <= 0 || headerSize > dibData.Length)
            {
                return 0;
            }

            if (headerSize < 40 || dibData.Length < 40)
            {
                return headerSize;
            }

            ushort bitCount = BitConverter.ToUInt16(dibData, 14);
            uint compression = BitConverter.ToUInt32(dibData, 16);
            uint colorsUsed = BitConverter.ToUInt32(dibData, 32);
            int colorCount = colorsUsed > 0 ? (int)Math.Min(colorsUsed, 256u) : bitCount <= 8 ? 1 << bitCount : 0;
            int maskBytes = 0;
            if (headerSize == 40)
            {
                if (compression == 3 && (bitCount == 16 || bitCount == 32))
                {
                    maskBytes = 12;
                }
                else if (compression == 6 && bitCount == 32)
                {
                    maskBytes = 16;
                }
            }

            int offset = headerSize + colorCount * 4 + maskBytes;
            if (offset < 0 || offset > dibData.Length)
            {
                return headerSize;
            }

            return offset;
        }

        private static IntPtr AllocateGlobalBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return IntPtr.Zero;

            IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero) return IntPtr.Zero;

            IntPtr lpGlobal = GlobalLock(hGlobal);
            if (lpGlobal == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.Copy(bytes, 0, lpGlobal, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            return hGlobal;
        }

        private static IntPtr AllocateGlobalString(string text)
        {
            if (string.IsNullOrEmpty(text)) return IntPtr.Zero;
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            return AllocateGlobalBytes(bytes);
        }

        private static IntPtr AllocateGlobalDropFiles(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return IntPtr.Zero;
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath);
                const int headerSize = 20;
                int totalBytes = headerSize + pathBytes.Length + 4;
                byte[] buffer = new byte[totalBytes];

                BitConverter.GetBytes((uint)headerSize).CopyTo(buffer, 0);
                BitConverter.GetBytes(1).CopyTo(buffer, 16);

                Buffer.BlockCopy(pathBytes, 0, buffer, headerSize, pathBytes.Length);
                return AllocateGlobalBytes(buffer);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static bool SetClipboardBytes(uint format, byte[] bytes)
        {
            if (format == 0 || bytes == null || bytes.Length == 0) return false;

            IntPtr hGlobal = AllocateGlobalBytes(bytes);
            if (hGlobal == IntPtr.Zero) return false;

            if (SetClipboardData(format, hGlobal) != IntPtr.Zero)
            {
                return true;
            }

            GlobalFree(hGlobal);
            return false;
        }

        public static async Task SetImageAsync(Image image, bool markFreeSnipEditorImage = false)
        {
            ArgumentNullException.ThrowIfNull(image);
            var log = LogHelper.GetLogger(typeof(UiClipboard));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log.Info($"[STEP:START] SetImageAsync - Dimensions: ({image.Width}x{image.Height}), MarkEditorImage: {markFreeSnipEditorImage}");

            try
            {
                var encoded = await Task.Run(() => EncodeClipboardImage(image)).ConfigureAwait(false);
                byte[] dibBytes = encoded.DibBytes;
                byte[] bmpFullBytes = encoded.BmpFullBytes;
                byte[] pngBytes = encoded.PngBytes;
                byte[] dibV5Bytes = await Task.Run(() => HasAlphaChannel(image) ? CreateDibV5(image) : null).ConfigureAwait(false);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && dibBytes != null)
                {
                    bool success = false;
                    uint pngFormat = RegisterClipboardFormat("PNG");
                    uint imagePngFormat = RegisterClipboardFormat("image/png");
                    uint freeSnipFormat = markFreeSnipEditorImage ? RegisterClipboardFormat(FreeSnipEditorImageFormat) : 0;

                    IntPtr hDib = AllocateGlobalBytes(dibBytes);
                    IntPtr hDibV5 = AllocateGlobalBytes(dibV5Bytes);
                    IntPtr hPng = AllocateGlobalBytes(pngBytes);
                    IntPtr hImagePng = AllocateGlobalBytes(pngBytes);
                    IntPtr hFreeSnip = markFreeSnipEditorImage ? AllocateGlobalBytes(FreeSnipEditorImageBytes) : IntPtr.Zero;

                    bool dibSet = false;
                    bool dibV5Set = false;
                    bool pngSet = false;
                    bool imagePngSet = false;
                    bool freeSnipSet = false;

                    try
                    {
                        success = TryWriteNativeClipboard(() =>
                        {
                            if (hDib != IntPtr.Zero && SetClipboardData(CF_DIB, hDib) != IntPtr.Zero)
                                dibSet = true;
                            else
                                return false;

                            if (hDibV5 != IntPtr.Zero && SetClipboardData(CF_DIBV5, hDibV5) != IntPtr.Zero)
                                dibV5Set = true;

                            if (pngFormat != 0 && hPng != IntPtr.Zero && SetClipboardData(pngFormat, hPng) != IntPtr.Zero)
                                pngSet = true;

                            if (imagePngFormat != 0 && hImagePng != IntPtr.Zero && SetClipboardData(imagePngFormat, hImagePng) != IntPtr.Zero)
                                imagePngSet = true;

                            if (freeSnipFormat != 0 && hFreeSnip != IntPtr.Zero && SetClipboardData(freeSnipFormat, hFreeSnip) != IntPtr.Zero)
                                freeSnipSet = true;

                            return dibSet;
                        });
                    }
                    finally
                    {
                        if (!dibSet && hDib != IntPtr.Zero) GlobalFree(hDib);
                        if (!dibV5Set && hDibV5 != IntPtr.Zero) GlobalFree(hDibV5);
                        if (!pngSet && hPng != IntPtr.Zero) GlobalFree(hPng);
                        if (!imagePngSet && hImagePng != IntPtr.Zero) GlobalFree(hImagePng);
                        if (!freeSnipSet && hFreeSnip != IntPtr.Zero) GlobalFree(hFreeSnip);
                    }

                    if (success)
                    {
                        log.Info($"[STEP:SUCCESS] SetImageAsync ({sw.ElapsedMilliseconds}ms) - CF_DIB and native image formats written.");
                        return;
                    }
                }

                var clipboard = GetClipboard();
                if (clipboard != null)
                {
                    var dataObject = new DataObject();
                    dataObject.Set("PNG", pngBytes);
                    dataObject.Set("image/png", pngBytes);
                    dataObject.Set("Bitmap", bmpFullBytes);
                    if (markFreeSnipEditorImage)
                    {
                        dataObject.Set(FreeSnipEditorImageFormat, FreeSnipEditorImageBytes);
                    }
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await clipboard.SetDataObjectAsync(dataObject);
                    });
                    log.Info($"[STEP:SUCCESS] SetImageAsync ({sw.ElapsedMilliseconds}ms) - Avalonia DataObject set.");
                    return;
                }
                log.Error($"[STEP:FAIL] SetImageAsync ({sw.ElapsedMilliseconds}ms) - No clipboard available.");
                throw new InvalidOperationException("The picture could not be copied to the clipboard. Please try again.");
            }
            catch (Exception ex)
            {
                log.Error($"[STEP:FAIL] SetImageAsync ({sw.ElapsedMilliseconds}ms) - Failed to set image to clipboard", ex);
                throw;
            }
        }

        private static bool HasAlphaChannel(Image image)
        {
            if (image == null) return false;

            if (image is Image<Rgba32> rgba32)
            {
                return CheckRgba32Alpha(rgba32);
            }

            if (image is Image<Bgra32> bgra32)
            {
                return CheckBgra32Alpha(bgra32);
            }

            if (image.PixelType.BitsPerPixel == 32)
            {
                using var clone = image.CloneAs<Rgba32>();
                return CheckRgba32Alpha(clone);
            }

            return false;
        }

        private static bool CheckRgba32Alpha(Image<Rgba32> img)
        {
            bool hasAlpha = false;
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (row[x].A < 255)
                        {
                            hasAlpha = true;
                            return;
                        }
                    }
                }
            });
            return hasAlpha;
        }

        private static bool CheckBgra32Alpha(Image<Bgra32> img)
        {
            bool hasAlpha = false;
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (row[x].A < 255)
                        {
                            hasAlpha = true;
                            return;
                        }
                    }
                }
            });
            return hasAlpha;
        }

        private static byte[] CreateDibV5(Image image)
        {
            int width = image.Width;
            int height = image.Height;
            const int headerSize = 124;
            int imageSize = checked(width * height * 4);
            byte[] dibV5 = new byte[checked(headerSize + imageSize)];

            using (var ms = new MemoryStream(dibV5))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((uint)headerSize);
                bw.Write((int)width);
                bw.Write((int)height);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)3);
                bw.Write((uint)imageSize);
                bw.Write((int)0);
                bw.Write((int)0);
                bw.Write((uint)0);
                bw.Write((uint)0);
                bw.Write((uint)0x00FF0000);
                bw.Write((uint)0x0000FF00);
                bw.Write((uint)0x000000FF);
                bw.Write((uint)0xFF000000);
                bw.Write((uint)0x73524742);
                for (int i = 0; i < 9; i++) bw.Write((int)0);
                bw.Write((uint)0);
                bw.Write((uint)0);
                bw.Write((uint)0);
                bw.Write((uint)4);
                bw.Write((uint)0);
                bw.Write((uint)0);
                bw.Write((uint)0);
            }

            Image<Bgra32> bgraImage = image as Image<Bgra32>;
            bool disposeBgra = false;
            if (bgraImage == null)
            {
                bgraImage = image.CloneAs<Bgra32>();
                disposeBgra = true;
            }

            try
            {
                int rowStride = checked(width * 4);
                int destOffset = headerSize;
                bgraImage.ProcessPixelRows(accessor =>
                {
                    for (int y = height - 1; y >= 0; y--)
                    {
                        var rowSpan = accessor.GetRowSpan(y);
                        MemoryMarshal.AsBytes(rowSpan).CopyTo(dibV5.AsSpan(destOffset, rowStride));
                        destOffset += rowStride;
                    }
                });
            }
            finally
            {
                if (disposeBgra)
                {
                    bgraImage.Dispose();
                }
            }

            return dibV5;
        }

        private static (byte[] DibBytes, byte[] BmpFullBytes, byte[] PngBytes) EncodeClipboardImage(Image image)
        {
            byte[] dibBytes = null;
            byte[] bmpFullBytes;
            byte[] pngBytes;

            using (var ms = new MemoryStream())
            {
                image.Save(ms, new PngEncoder());
                pngBytes = ms.ToArray();

                ms.SetLength(0);
                image.Save(ms, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });
                bmpFullBytes = ms.ToArray();

                if (bmpFullBytes.Length > 14)
                {
                    dibBytes = new byte[bmpFullBytes.Length - 14];
                    Buffer.BlockCopy(bmpFullBytes, 14, dibBytes, 0, dibBytes.Length);
                }
            }

            return (dibBytes, bmpFullBytes, pngBytes);
        }
    }
}
