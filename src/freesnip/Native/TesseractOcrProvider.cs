using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        private const int WorkerMagic = 0x46534F43;
        private const int MaxWorkerPngBytes = 128 * 1024 * 1024;
        private const int MaxWorkerWords = 1_000_000;

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
        }

        private static async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            await OcrInstallationHelper.EnsureTessDataReadyAsync(cancellationToken).ConfigureAwait(false);
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
            if (pngBytes == null || pngBytes.Length == 0) return null;
            if (pngBytes.Length > MaxWorkerPngBytes) throw new InvalidDataException("OCR image exceeds the worker IPC payload limit.");

            var startInfo = new ProcessStartInfo
            {
                FileName = RuntimePathHelper.ExecutablePath,
                Arguments = "--tesseract-ocr-worker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new IOException("Could not start the isolated Tesseract worker process.");

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task writeTask = Task.Run(() => WriteWorkerRequest(process.StandardInput.BaseStream, tessDataPath, pngBytes, segmentation, adaptiveThreshold), CancellationToken.None);
            Task<TesseractRawResult> readTask = Task.Run(() => ReadWorkerResponse(process.StandardOutput.BaseStream), CancellationToken.None);

            try
            {
                await writeTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();

                TesseractRawResult result = await readTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0 && result == null)
                {
                    string stderr = await stderrTask.ConfigureAwait(false);
                    throw new IOException($"Tesseract worker exited with code {process.ExitCode}: {stderr}");
                }
                return result;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                TryKillWorker(process);
                if (ex is TimeoutException) throw;
                throw;
            }
            catch
            {
                TryKillWorker(process);
                throw;
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.HasExited) TryKillWorker(process);
            }
        }

        private static void WriteWorkerRequest(Stream stream, string tessDataPath, byte[] pngBytes, PageSegMode segmentation, bool adaptiveThreshold)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(WorkerMagic);
            writer.Write(tessDataPath ?? string.Empty);
            writer.Write((int)segmentation);
            writer.Write(adaptiveThreshold);
            writer.Write(pngBytes.Length);
            writer.Write(pngBytes);
            writer.Flush();
        }

        private static TesseractRawResult ReadWorkerResponse(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            int magic = reader.ReadInt32();
            if (magic != WorkerMagic) throw new InvalidDataException("Invalid response from Tesseract worker.");
            bool success = reader.ReadBoolean();
            if (!success)
            {
                string workerError = reader.ReadString();
                throw new IOException("Tesseract worker failed: " + workerError);
            }

            var result = new TesseractRawResult
            {
                Text = reader.ReadString(),
                Words = new List<TesseractRawWord>()
            };
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxWorkerWords) throw new InvalidDataException("Invalid Tesseract worker word count.");
            for (int i = 0; i < count; i++)
            {
                result.Words.Add(new TesseractRawWord
                {
                    Text = reader.ReadString(),
                    X = reader.ReadInt32(),
                    Y = reader.ReadInt32(),
                    Width = reader.ReadInt32(),
                    Height = reader.ReadInt32(),
                    Confidence = reader.ReadSingle()
                });
            }
            return result;
        }

        private static void WriteWorkerResponse(Stream stream, TesseractRawResult result)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(WorkerMagic);
            writer.Write(true);
            writer.Write(result?.Text ?? string.Empty);
            int count = result?.Words?.Count ?? 0;
            writer.Write(count);
            if (result?.Words != null)
            {
                foreach (var word in result.Words)
                {
                    writer.Write(word.Text ?? string.Empty);
                    writer.Write(word.X);
                    writer.Write(word.Y);
                    writer.Write(word.Width);
                    writer.Write(word.Height);
                    writer.Write(word.Confidence);
                }
            }
            writer.Flush();
        }

        private static void WriteWorkerError(Stream stream, Exception ex)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(WorkerMagic);
            writer.Write(false);
            writer.Write(ex?.Message ?? "Unknown OCR worker failure.");
            writer.Flush();
        }

        internal static async Task<int> RunWorkerAsync(Stream input, Stream output, CancellationToken cancellationToken)
        {
            try
            {
                await OcrInstallationHelper.EnsureTesseractReadyAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
                int magic = reader.ReadInt32();
                if (magic != WorkerMagic) throw new InvalidDataException("Invalid Tesseract worker request.");
                string tessDataPath = reader.ReadString();
                var segmentation = (PageSegMode)reader.ReadInt32();
                bool adaptiveThreshold = reader.ReadBoolean();
                int length = reader.ReadInt32();
                if (length <= 0 || length > MaxWorkerPngBytes) throw new InvalidDataException("Invalid Tesseract worker image length.");
                byte[] pngBytes = reader.ReadBytes(length);
                if (pngBytes.Length != length) throw new EndOfStreamException("Incomplete Tesseract worker image payload.");

                TesseractRawResult raw = RecognizePngInWorker(tessDataPath, pngBytes, segmentation, adaptiveThreshold, cancellationToken);
                WriteWorkerResponse(output, raw);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    WriteWorkerError(output, ex);
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                return 1;
            }
        }

        private static TesseractRawResult RecognizePngInWorker(string tessDataPath, byte[] pngBytes, PageSegMode segmentation, bool adaptiveThreshold, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using TesseractEngine engine = CreateEngine(tessDataPath, adaptiveThreshold);
            using Pix pix = Pix.LoadFromMemory(pngBytes);
            using Page page = engine.Process(pix, segmentation);
            return ReadPage(page);
        }

        private static void TryKillWorker(Process process)
        {
            try
            {
                if (process != null && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
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
