#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using freesnip.foundation.core;
using freesnip.foundation.interfaces.Ocr;

namespace freesnip.editor.Services
{
    public enum EditorOcrStatus
    {
        Unavailable,
        Recognized
    }

    public readonly record struct EditorOcrOutcome(EditorOcrStatus Status, OcrInformation? Info);

    /// <summary>
    /// OCR orchestration for the editor: provider selection, recognition and the plain-text backup file.
    /// The editor window only maps the outcome to UI. Providers are injected so this runs without Avalonia.
    /// </summary>
    public sealed class EditorOcrService
    {
        private readonly Func<IEnumerable<IOcrProvider>> _providerSource;
        private readonly string _backupDirectory;

        public EditorOcrService()
            : this(() => SimpleServiceProvider.Current.GetAllInstances<IOcrProvider>(), Path.Combine(Path.GetTempPath(), "FreeSnip"))
        {
        }

        public EditorOcrService(Func<IEnumerable<IOcrProvider>> providerSource, string backupDirectory)
        {
            _providerSource = providerSource ?? throw new ArgumentNullException(nameof(providerSource));
            _backupDirectory = backupDirectory ?? throw new ArgumentNullException(nameof(backupDirectory));
        }

        public async Task<EditorOcrOutcome> RecognizeAsync(Image image, string? configuredEngine, CancellationToken cancellationToken = default)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            IOcrProvider? provider = OcrProviderSelector.Select(_providerSource(), configuredEngine);
            if (provider == null) return new EditorOcrOutcome(EditorOcrStatus.Unavailable, null);

            OcrInformation? info = await provider.DoOcrAsync(image, cancellationToken).ConfigureAwait(false);
            return info == null
                ? new EditorOcrOutcome(EditorOcrStatus.Unavailable, null)
                : new EditorOcrOutcome(EditorOcrStatus.Recognized, info);
        }

        /// <summary>Writes the recognized text to a timestamped file and returns its full path.</summary>
        public async Task<string> SaveTextBackupAsync(string text, DateTime timestamp, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => Directory.CreateDirectory(_backupDirectory), cancellationToken).ConfigureAwait(false);
            string fullPath = Path.Combine(_backupDirectory, $"OCR_{timestamp:yyyy-MM-dd_HH-mm-ss_fff}.txt");
            await File.WriteAllTextAsync(fullPath, text ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return fullPath;
        }
    }
}
