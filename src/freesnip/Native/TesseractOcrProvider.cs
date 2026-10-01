using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using freesnip.native.foundation;
using freesnip.foundation.Interfaces;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using Tesseract;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;

namespace freesnip.native
{
    public sealed class TesseractOcrProvider : IOcrProvider, IDisposable, IAsyncDisposable
    {
        private readonly OcrRequestQueue _queue;
        private int _disposed;
        private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(45);
        private static readonly SemaphoreSlim NativeOcrGate = new SemaphoreSlim(1, 1);
        private static TesseractEngine _sharedEngine;
        private static string _sharedEnginePath;
        private static bool _sharedEngineAdaptive;

        public TesseractOcrProvider()
        {
            _queue = new OcrRequestQueue(RecognizeCoreAsync);
        }

        public string EngineId => "tesseract";

        public string DisplayName => "Tesseract (External OCR Engine)";

        public bool HasRequiredLanguages()
        {
            string tessDataPath = OcrInstallationHelper.GetTessDataDirectory();
            return HasTessData(tessDataPath, "eng.traineddata") && HasTessData(tessDataPath, "heb.traineddata");
        }

        public Task<OcrInformation> DoOcrAsync(Image image) => DoOcrAsync(image, CancellationToken.None);

        public Task<OcrInformation> DoOcrAsync(Image image, CancellationToken ct, bool isAlreadyOwned = false)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(TesseractOcrProvider));
            return _queue.EnqueueAsync(image, ct, isAlreadyOwned);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _queue.DisposeAsync().ConfigureAwait(false);
            await NativeOcrGate.WaitAsync().ConfigureAwait(false);
            try
            {
                DisposeSharedEngine();
            }
            finally
            {
                NativeOcrGate.Release();
            }
        }

        private static async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            await OcrInstallationHelper.EnsureTesseractReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool HasTessData(string tessDataPath, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tessDataPath) || !Directory.Exists(tessDataPath))
                {
                    return false;
                }

                string path = Path.Combine(tessDataPath, fileName);
                return File.Exists(path) && new FileInfo(path).Length >= 128 * 1024;
            }
            catch
            {
                return false;
            }
        }

        private async Task<OcrInformation> RecognizeCoreAsync(Image image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

            string tessDataPath = OcrInstallationHelper.GetTessDataDirectory();
            if (!HasTessData(tessDataPath, "eng.traineddata") || !HasTessData(tessDataPath, "heb.traineddata"))
            {
                return null;
            }

            using OcrPreparedImage prepared = OcrImagePreprocessor.Prepare(image, OcrPreprocessingProfile.Tesseract);
            if (prepared?.Image == null)
            {
                return null;
            }

            byte[] pngBytes;
            using (var buffer = new MemoryStream())
            {
                await prepared.Image.SaveAsync(buffer, new PngEncoder(), cancellationToken).ConfigureAwait(false);
                pngBytes = buffer.ToArray();
            }

            PageSegMode segmentation = ResolveSegmentation(image.Width, image.Height);
            bool adaptiveThreshold = false;
            try
            {
                adaptiveThreshold = IniConfig.GetIniSection<CoreConfiguration>().OcrAdaptiveThreshold;
            }
            catch
            {
            }

            try
            {
                var raw = await RecognizePngAsync(tessDataPath, pngBytes, segmentation, adaptiveThreshold, RecognitionTimeout, cancellationToken).ConfigureAwait(false);
                return MapRawResult(raw, prepared);
            }
            catch (TimeoutException ex)
            {
                ExecutionTrace.LogException("TesseractOcr.Timeout", ex, tessDataPath);
                return null;
            }
        }

        private static async Task<TesseractRawResult> RecognizePngAsync(string tessDataPath, byte[] pngBytes, PageSegMode segmentation, bool adaptiveThreshold, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await NativeOcrGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<TesseractRawResult> work = null;
            try
            {
                work = Task.Run(() => RecognizePng(tessDataPath, pngBytes, segmentation, adaptiveThreshold, cancellationToken));
                return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (work == null)
                {
                    NativeOcrGate.Release();
                }
                else
                {
                    _ = work.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        NativeOcrGate.Release();
                    }, TaskScheduler.Default);
                }
            }
        }

        private static TesseractRawResult RecognizePng(string tessDataPath, byte[] pngBytes, PageSegMode segmentation, bool adaptiveThreshold, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TesseractEngine engine = AcquireSharedEngine(tessDataPath, adaptiveThreshold);
            try
            {
                using Pix pix = Pix.LoadFromMemory(pngBytes);
                using Page page = engine.Process(pix, segmentation);
                return ReadPage(page);
            }
            catch
            {
                DisposeSharedEngine();
                throw;
            }
        }

        private static TesseractEngine AcquireSharedEngine(string tessDataPath, bool adaptiveThreshold)
        {
            if (_sharedEngine != null
                && string.Equals(_sharedEnginePath, tessDataPath, StringComparison.OrdinalIgnoreCase)
                && _sharedEngineAdaptive == adaptiveThreshold)
            {
                return _sharedEngine;
            }

            DisposeSharedEngine();
            _sharedEngine = CreateEngine(tessDataPath, adaptiveThreshold);
            _sharedEnginePath = tessDataPath;
            _sharedEngineAdaptive = adaptiveThreshold;
            return _sharedEngine;
        }

        private static void DisposeSharedEngine()
        {
            try
            {
                _sharedEngine?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _sharedEngine = null;
                _sharedEnginePath = null;
            }
        }

        private static PageSegMode ResolveSegmentation(int width, int height)
        {
            if (width <= 0 || height <= 0) return PageSegMode.Auto;
            double aspect = width / (double)height;
            if (height <= 64 && aspect >= 5.0) return PageSegMode.SingleLine;
            if (height <= 48 && width <= 220 && aspect < 5.0) return PageSegMode.SingleWord;
            return PageSegMode.Auto;
        }

        private static TesseractEngine CreateEngine(string tessDataPath, bool adaptiveThreshold)
        {
            var engine = new TesseractEngine(tessDataPath, "heb+eng", EngineMode.LstmOnly);
            engine.SetVariable("preserve_interword_spaces", "1");
            engine.SetVariable("load_system_dawg", false);
            engine.SetVariable("load_freq_dawg", false);
            engine.SetVariable("classify_enable_learning", false);
            engine.SetVariable("user_defined_dpi", "300");
            engine.SetVariable("textord_tabfind_find_tables", false);
            if (adaptiveThreshold)
            {
                engine.SetVariable("thresholding_method", "2");
            }
            return engine;
        }

        private static TesseractRawResult ReadPage(Page page)
        {
            if (page == null)
            {
                return null;
            }

            var result = new TesseractRawResult
            {
                Text = page.GetText() ?? string.Empty,
                Words = new List<TesseractRawWord>()
            };

            using var iterator = page.GetIterator();
            iterator.Begin();
            do
            {
                if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
                {
                    continue;
                }

                string text = iterator.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                float confidence;
                try
                {
                    confidence = iterator.GetConfidence(PageIteratorLevel.Word) / 100f;
                }
                catch
                {
                    confidence = OcrWord.UnknownConfidence;
                }

                result.Words.Add(new TesseractRawWord
                {
                    Text = text.Trim(),
                    X = rect.X1,
                    Y = rect.Y1,
                    Width = rect.X2 - rect.X1,
                    Height = rect.Y2 - rect.Y1,
                    Confidence = confidence
                });
            }
            while (iterator.Next(PageIteratorLevel.Word));

            return result;
        }

        private static OcrInformation MapRawResult(TesseractRawResult raw, OcrPreparedImage prepared)
        {
            if (raw == null)
            {
                return null;
            }

            var information = new OcrInformation
            {
                Text = raw.Text ?? string.Empty,
                Words = new List<OcrWord>()
            };

            foreach (var word in raw.Words)
            {
                information.Words.Add(new OcrWord
                {
                    Text = word.Text,
                    Bounds = prepared.MapBounds(word.X, word.Y, word.Width, word.Height),
                    Confidence = word.Confidence
                });
            }

            OcrTextLayout.NormalizeTextFromWordsWhenEmpty(information);
            return information;
        }

        private sealed class TesseractRawResult
        {
            public string Text { get; set; }
            public List<TesseractRawWord> Words { get; set; }
        }

        private sealed class TesseractRawWord
        {
            public string Text { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public float Confidence { get; set; } = OcrWord.UnknownConfidence;
        }
    }
}

