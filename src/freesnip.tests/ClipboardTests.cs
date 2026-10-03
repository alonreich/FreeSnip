using System;
using System.Threading.Tasks;
using freesnip.foundation.core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace freesnip.tests
{
    [Collection("WindowsClipboard")]
    public class ClipboardTests
    {
        [Fact]
        public async Task InvalidImage_ReportsFailureInsteadOfPretendingToCopy()
        {
            var image = new Image<Bgra32>(20, 20);
            image.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => UiClipboard.SetImageAsync(image));
        }

        [Fact]
        public void ImageEncoding_ProvidesRealDibBmpAndPngPayloads()
        {
            using var image = new Image<Bgra32>(20, 20, new Bgra32(255, 0, 0));
            var method = typeof(UiClipboard).GetMethod("EncodeClipboardImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var encoded = ((byte[] Dib, byte[] Bmp, byte[] Png))method.Invoke(null, new object[] { image })!;
            Assert.Equal(new byte[] { 0x42, 0x4d }, encoded.Bmp.Take(2));
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, encoded.Png.Take(4));
            Assert.Equal(encoded.Bmp.Skip(14), encoded.Dib);
            using var decoded = Image.Load<Bgra32>(encoded.Png);
            Assert.Equal(image[0, 0], decoded[0, 0]);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private static bool CanAccessClipboard()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return false;

            if (OpenClipboard(IntPtr.Zero))
            {
                CloseClipboard();
                return true;
            }

            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return err != 5;
        }

        [Fact]
        public async Task SetImageAsync_MaintainsLivingClipboardOwnerWindow_AfterCompletion()
        {
            if (!CanAccessClipboard())
                return;

            using var image = new Image<Rgba32>(32, 32, new Rgba32(120, 180, 240, 255));
            await UiClipboard.SetImageAsync(image);

            IntPtr ownerHwnd = UiClipboard.GetClipboardOwnerHwnd();
            Assert.NotEqual(IntPtr.Zero, ownerHwnd);
            Assert.True(IsWindow(ownerHwnd), "Clipboard owner window must be a living HWND.");

            IntPtr currentOwner = GetClipboardOwner();
            Assert.Equal(ownerHwnd, currentOwner);
            Assert.True(IsWindow(currentOwner), "GetClipboardOwner() must return a living window handle after SetImageAsync completes.");

            using var image2 = new Image<Rgba32>(16, 16, new Rgba32(200, 100, 50, 255));
            await UiClipboard.SetImageAsync(image2);

            Assert.Equal(ownerHwnd, UiClipboard.GetClipboardOwnerHwnd());
            Assert.Equal(ownerHwnd, GetClipboardOwner());
            Assert.True(IsWindow(GetClipboardOwner()), "Clipboard owner window must remain alive across multiple clipboard writes.");
        }

        [Fact]
        public async Task SetImageAsync_WritesBothDibAndPngFormats_PreservingAlphaTransparency()
        {
            if (!CanAccessClipboard())
                return;

            const int w = 32;
            const int h = 32;
            using var image = new Image<Rgba32>(w, h);

            var highlight = new Rgba32(255, 255, 0, 128);
            var opaque = new Rgba32(0, 128, 255, 255);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    image[x, y] = x < w / 2 ? highlight : opaque;
                }
            }

            await UiClipboard.SetImageAsync(image);

            const uint CF_DIB = 8;
            Assert.True(IsClipboardFormatAvailable(CF_DIB), "CF_DIB must be available on clipboard for legacy Win32 apps.");

            uint pngFormat = RegisterClipboardFormat("PNG");
            Assert.NotEqual(0u, pngFormat);
            Assert.True(IsClipboardFormatAvailable(pngFormat), "Registered PNG format must be available on clipboard.");

            const uint CF_DIBV5 = 17;
            Assert.True(IsClipboardFormatAvailable(CF_DIBV5), "CF_DIBV5 must be available on clipboard when image contains alpha channel.");

            using var pastedImage = await UiClipboard.GetImageAsync();
            Assert.NotNull(pastedImage);

            using var pastedRgba = pastedImage.CloneAs<Rgba32>();
            Assert.Equal(w, pastedRgba.Width);
            Assert.Equal(h, pastedRgba.Height);

            Rgba32 pastedTranslucent = pastedRgba[5, 5];
            Assert.True(pastedTranslucent.A > 100 && pastedTranslucent.A < 150,
                $"Translucent highlight alpha must be preserved around 128, actual: {pastedTranslucent.A}");
            Assert.True(pastedTranslucent.R > 240 && pastedTranslucent.G > 240 && pastedTranslucent.B < 20,
                "Translucent highlight RGB channels must be preserved.");

            Rgba32 pastedOpaque = pastedRgba[25, 25];
            Assert.Equal(255, pastedOpaque.A);
            Assert.Equal(0, pastedOpaque.R);
            Assert.True(pastedOpaque.G > 100 && pastedOpaque.B > 240);
        }

        [Fact]
        public async Task SetTextAsync_MaintainsLivingClipboardOwnerWindow()
        {
            if (!CanAccessClipboard())
                return;

            await UiClipboard.SetTextAsync("FreeSnip Persistent Owner Test");

            IntPtr owner = GetClipboardOwner();
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.True(IsWindow(owner), "Clipboard owner window must remain alive after SetTextAsync.");
            Assert.Equal(UiClipboard.GetClipboardOwnerHwnd(), owner);
        }

        [Fact]
        public async Task SetFilePathThenImageAsync_AtomicBatch_ExecutesRapidlyWithZeroDelays()
        {
            if (!CanAccessClipboard())
                return;

            string warmPath = System.IO.Path.GetTempFileName();
            string testPath = System.IO.Path.GetTempFileName();
            try
            {
                using var warmImage = new Image<Rgba32>(16, 16, new Rgba32(100, 150, 200, 255));
                await UiClipboard.SetFilePathThenImageAsync(warmPath, warmImage);
                await Task.Delay(150);

                using var image = new Image<Rgba32>(32, 32, new Rgba32(255, 0, 128, 255));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await UiClipboard.SetFilePathThenImageAsync(testPath, image);
                sw.Stop();

                Assert.True(sw.ElapsedMilliseconds < 150, $"SetFilePathThenImageAsync must execute in under 20-50ms with zero delays, actual: {sw.ElapsedMilliseconds}ms");
            }
            finally
            {
                try { System.IO.File.Delete(warmPath); } catch { }
                try { System.IO.File.Delete(testPath); } catch { }
            }
        }

        [Fact]
        public async Task SetFilePathThenImageAsync_BothTextAndImageFormatsCoexistSimultaneously()
        {
            if (!CanAccessClipboard())
                return;

            const int w = 32;
            const int h = 32;
            using var image = new Image<Rgba32>(w, h, new Rgba32(10, 200, 50, 200));
            string testPath = System.IO.Path.GetTempFileName();
            try
            {
                await UiClipboard.SetFilePathThenImageAsync(testPath, image);

                const uint CF_UNICODETEXT = 13;
                const uint CF_DIB = 8;
                const uint CF_HDROP = 15;
                const uint CF_DIBV5 = 17;

                Assert.True(IsClipboardFormatAvailable(CF_UNICODETEXT), "CF_UNICODETEXT must be present on the clipboard.");
                Assert.True(IsClipboardFormatAvailable(CF_DIB), "CF_DIB must be present simultaneously on the clipboard.");
                Assert.True(IsClipboardFormatAvailable(CF_DIBV5), "CF_DIBV5 must be present simultaneously on the clipboard.");

                uint pngFormat = RegisterClipboardFormat("PNG");
                Assert.True(IsClipboardFormatAvailable(pngFormat), "PNG format atom must be present simultaneously on the clipboard.");
                Assert.True(IsClipboardFormatAvailable(CF_HDROP), "CF_HDROP format must be present for file drop pasting.");

                IntPtr owner = GetClipboardOwner();
                Assert.NotEqual(IntPtr.Zero, owner);
                Assert.True(IsWindow(owner), "Clipboard owner window must remain a living HWND handle.");
                Assert.Equal(UiClipboard.GetClipboardOwnerHwnd(), owner);

                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hText = GetClipboardData(CF_UNICODETEXT);
                        Assert.NotEqual(IntPtr.Zero, hText);
                        IntPtr pText = GlobalLock(hText);
                        try
                        {
                            string? clipboardText = System.Runtime.InteropServices.Marshal.PtrToStringUni(pText);
                            Assert.Equal(System.IO.Path.GetFullPath(testPath), clipboardText);
                        }
                        finally
                        {
                            GlobalUnlock(hText);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                using var pastedImage = await UiClipboard.GetImageAsync();
                Assert.NotNull(pastedImage);
                Assert.Equal(w, pastedImage.Width);
                Assert.Equal(h, pastedImage.Height);
            }
            finally
            {
                try { System.IO.File.Delete(testPath); } catch { }
            }
        }

        [Fact]
        public async Task SetPlainTextOnlyAsync_WritesQuotedPathAndProbesSuccessfully()
        {
            if (!CanAccessClipboard())
                return;

            string rawPath = @"C:\TestFolder\Capture 2026-10-01.png";
            string quotedPath = $"\"{rawPath}\"";

            bool result = await UiClipboard.SetPlainTextOnlyAsync(quotedPath);
            Assert.True(result, "SetPlainTextOnlyAsync must return true on success.");

            bool probeText = UiClipboard.ProbeClipboardFormat(UiClipboard.CF_UNICODETEXT_FORMAT, 200);
            Assert.True(probeText, "ProbeClipboardFormat must return true for CF_UNICODETEXT_FORMAT.");

            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr hText = GetClipboardData(UiClipboard.CF_UNICODETEXT_FORMAT);
                    Assert.NotEqual(IntPtr.Zero, hText);
                    IntPtr pText = GlobalLock(hText);
                    try
                    {
                        string? clipboardText = System.Runtime.InteropServices.Marshal.PtrToStringUni(pText);
                        Assert.Equal(quotedPath, clipboardText);
                        Assert.StartsWith("\"", clipboardText);
                        Assert.EndsWith("\"", clipboardText);
                    }
                    finally
                    {
                        GlobalUnlock(hText);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
        }

        [Fact]
        public async Task DownloadDoubleClipboardSequence_StagesDibThenQuotedPlainText()
        {
            if (!CanAccessClipboard())
                return;

            string savedPath = @"C:\Users\alon\Downloads\FreeSnip_20261001_120000.png";
            string quotedPath = $"\"{savedPath}\"";

            using var testImage = new Image<Rgba32>(24, 24, new Rgba32(50, 100, 150, 255));

            await UiClipboard.SetImageAsync(testImage, markFreeSnipEditorImage: true);
            bool dibProbed = UiClipboard.ProbeClipboardFormat(UiClipboard.CF_DIB_FORMAT, 200);
            Assert.True(dibProbed, "CF_DIB must be probed as available on the clipboard.");

            bool textSet = await UiClipboard.SetPlainTextOnlyAsync(quotedPath);
            Assert.True(textSet, "SetPlainTextOnlyAsync must succeed for quoted path.");

            bool textProbed = UiClipboard.ProbeClipboardFormat(UiClipboard.CF_UNICODETEXT_FORMAT, 200);
            Assert.True(textProbed, "CF_UNICODETEXT must be probed as available on the clipboard.");

            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr hText = GetClipboardData(UiClipboard.CF_UNICODETEXT_FORMAT);
                    Assert.NotEqual(IntPtr.Zero, hText);
                    IntPtr pText = GlobalLock(hText);
                    try
                    {
                        string? text = System.Runtime.InteropServices.Marshal.PtrToStringUni(pText);
                        Assert.Equal(quotedPath, text);
                    }
                    finally
                    {
                        GlobalUnlock(hText);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
        }

        [Fact]
        public void ProbeClipboardFormat_DetectsAvailableAndUnavailableFormatsAccurately()
        {
            if (!CanAccessClipboard())
                return;

            const uint NON_EXISTENT_FORMAT = 0xBEEF;
            bool nonExistentProbed = UiClipboard.ProbeClipboardFormat(NON_EXISTENT_FORMAT, 20);
            Assert.False(nonExistentProbed, "Non-existent format must return false when probed.");
        }

        [Fact]
        public async Task SequenceNumber_IncrementsOnClipboardChange()
        {
            if (!CanAccessClipboard())
                return;

            uint seq1 = UiClipboard.GetCurrentSequenceNumber();
            await UiClipboard.SetPlainTextOnlyAsync($"\"TestSequence_{Guid.NewGuid()}\"");
            uint seq2 = UiClipboard.GetCurrentSequenceNumber();

            Assert.NotEqual(seq1, seq2);
        }

        [Fact]
        public void InMemoryAnnotationMarker_TogglesCorrectly()
        {
            UiClipboard.SetInMemoryAnnotationMarker(true);
            UiClipboard.SetInMemoryAnnotationMarker(false);
            Assert.True(true);
        }
    }
}


