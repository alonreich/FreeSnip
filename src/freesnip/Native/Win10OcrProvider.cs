using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using freesnip.native.foundation;
using freesnip.foundation.Interfaces;
using freesnip.foundation.interfaces.Ocr;
using DomainOcrWord = freesnip.foundation.interfaces.Ocr.OcrWord;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace freesnip.native
{
    public sealed class Win10OcrProvider : IOcrProvider, IDisposable, IAsyncDisposable
    {
        private readonly OcrRequestQueue _queue;
        private int _disposed;
        private static readonly object EngineSync = new object();
        private static readonly Dictionary<string, OcrEngine> CachedEngines = new Dictionary<string, OcrEngine>(StringComparer.OrdinalIgnoreCase);

        public Win10OcrProvider()
        {
            _queue = new OcrRequestQueue(RecognizeCoreAsync);
        }

        public string EngineId => "windows";

        public string DisplayName => "Windows Native OCR Engine";

        public bool HasRequiredLanguages() => AreRequiredLanguagesAvailable();

        public Task<OcrInformation> DoOcrAsync(Image image) => DoOcrAsync(image, CancellationToken.None);

        public Task<OcrInformation> DoOcrAsync(Image image, CancellationToken ct, bool isAlreadyOwned = false) => _queue.EnqueueAsync(image, ct, isAlreadyOwned);

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

        public static bool AreRequiredLanguagesAvailable()
        {
            return IsEnglishLanguageAvailable();
        }

        public static bool IsEnglishLanguageAvailable()
        {
            return ResolveLanguageTag("en") != null;
        }

        public static Task EnsureWindowsOcrInstalled() => Task.CompletedTask;

        internal static Task<OcrInformation> RecognizeEnglishOnlyAsync(Image image, CancellationToken cancellationToken)
        {
            return RecognizeCoreAsync(image, cancellationToken, includeHebrew: false);
        }

        private static Task<OcrInformation> RecognizeCoreAsync(Image image, CancellationToken cancellationToken)
        {
            return RecognizeCoreAsync(image, cancellationToken, includeHebrew: true);
        }

        private static async Task<OcrInformation> RecognizeCoreAsync(Image image, CancellationToken cancellationToken, bool includeHebrew)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string englishTag = ResolveLanguageTag("en");
            string hebrewTag = includeHebrew ? ResolveLanguageTag("he") : null;
            if (englishTag == null)
            {
                return null;
            }

            using OcrPreparedImage prepared = OcrImagePreprocessor.Prepare(image, OcrPreprocessingProfile.Windows);
            if (prepared?.Image == null)
            {
                return null;
            }

            using SoftwareBitmap bitmap = await CreateSoftwareBitmapAsync(prepared.Image, cancellationToken).ConfigureAwait(false);
            if (bitmap == null)
            {
                return null;
            }

            Task<OcrInformation> englishTask = RecognizeWithLanguageAsync(bitmap, englishTag, prepared, cancellationToken);
            Task<OcrInformation> hebrewTask = (!includeHebrew || hebrewTag == null) ? Task.FromResult<OcrInformation>(null) : RecognizeWithLanguageAsync(bitmap, hebrewTag, prepared, cancellationToken);

            await Task.WhenAll(englishTask, hebrewTask).ConfigureAwait(false);

            OcrInformation english = await englishTask.ConfigureAwait(false);
            OcrInformation hebrew = await hebrewTask.ConfigureAwait(false);

            if (hebrew == null)
            {
                return english;
            }

            return OcrTextLayout.MergeByLanguage(hebrew, english);
        }

        private static async Task<OcrInformation> RecognizeWithLanguageAsync(SoftwareBitmap bitmap, string languageTag, OcrPreparedImage prepared, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(languageTag))
            {
                return null;
            }

            OcrEngine engine = GetOrCreateEngine(languageTag);
            if (engine == null)
            {
                return null;
            }

            OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            return MapResult(result, prepared);
        }

        private static OcrEngine GetOrCreateEngine(string languageTag)
        {
            lock (EngineSync)
            {
                if (CachedEngines.TryGetValue(languageTag, out var cached))
                {
                    return cached;
                }

                var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                if (engine != null)
                {
                    CachedEngines[languageTag] = engine;
                }

                return engine;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> CachedLanguageTags = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string ResolveLanguageTag(string prefix)
        {
            if (CachedLanguageTags.TryGetValue(prefix, out string cached)) return cached;
            try
            {
                string tag = OcrEngine.AvailableRecognizerLanguages
                    .Select(language => language.LanguageTag)
                    .FirstOrDefault(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (tag != null) CachedLanguageTags[prefix] = tag;
                return tag;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<SoftwareBitmap> CreateSoftwareBitmapAsync(Image image, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            await image.SaveAsync(stream, new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder(), cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            using IRandomAccessStream randomAccessStream = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken).ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);
        }

        private static OcrInformation MapResult(OcrResult result, OcrPreparedImage prepared)
        {
            if (result == null)
            {
                return null;
            }

            var information = new OcrInformation
            {
                Text = result.Text ?? string.Empty,
                Words = new List<DomainOcrWord>()
            };

            if (result.Lines != null)
            {
                foreach (var line in result.Lines)
                {
                    if (line?.Words == null)
                    {
                        continue;
                    }

                    foreach (var word in line.Words)
                    {
                        if (word == null || string.IsNullOrWhiteSpace(word.Text))
                        {
                            continue;
                        }

                        var bounds = word.BoundingRect;
                        information.Words.Add(new DomainOcrWord
                        {
                            Text = word.Text,
                            Bounds = prepared.MapBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height)
                        });
                    }
                }
            }

            OcrTextLayout.NormalizeTextFromWordsWhenEmpty(information);
            return information;
        }
    }
}


