#if USE_TESSERACT
using System;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace freesnip.native
{
    public sealed class MixedLanguageOcrProvider : IOcrProvider, IDisposable, IAsyncDisposable
    {
        private readonly TesseractOcrProvider _tesseract;
        private readonly OcrRequestQueue _queue;
        private int _disposed;

        public MixedLanguageOcrProvider(TesseractOcrProvider tesseract)
        {
            _tesseract = tesseract ?? throw new ArgumentNullException(nameof(tesseract));
            _queue = new OcrRequestQueue(RecognizeCoreAsync);
        }

        public const string ProviderDisplayName = "Tesseract + Windows (Mixed EN/HE)";

        public string EngineId => "mixed";

        public string DisplayName => ProviderDisplayName;

        public bool HasRequiredLanguages()
        {
            return Win10OcrProvider.IsEnglishLanguageAvailable() && _tesseract.HasRequiredLanguages();
        }

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

        private async Task<OcrInformation> RecognizeCoreAsync(Image image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            Image cloneForTesseract = await Task.Run(() => image.Clone(x => { }), cancellationToken).ConfigureAwait(false);
            Task<OcrInformation> englishTask;
            try
            {
                englishTask = Win10OcrProvider.RecognizeEnglishOnlyAsync(image, cancellationToken);
            }
            catch
            {
                cloneForTesseract?.Dispose();
                throw;
            }
            Task<OcrInformation> tesseractTask = _tesseract.DoOcrAsync(cloneForTesseract, cancellationToken, true);
            await Task.WhenAll(englishTask, tesseractTask).ConfigureAwait(false);
            OcrInformation english = await englishTask.ConfigureAwait(false);
            OcrInformation tesseract = await tesseractTask.ConfigureAwait(false);
            return OcrTextLayout.MergeByLanguage(tesseract, english);
        }
    }
}
#endif
