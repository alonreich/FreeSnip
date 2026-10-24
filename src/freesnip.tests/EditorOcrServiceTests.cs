using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using freesnip.editor.Services;
using freesnip.foundation.interfaces.Ocr;
using Xunit;

namespace freesnip.tests
{
    public class EditorOcrServiceTests
    {
        private sealed class FakeProvider : IOcrProvider
        {
            private readonly OcrInformation _result;
            public FakeProvider(string engineId, OcrInformation result) { EngineId = engineId; _result = result; }
            public string EngineId { get; }
            public string DisplayName => EngineId;
            public bool HasRequiredLanguages() => true;
            public Task<OcrInformation> DoOcrAsync(Image image, CancellationToken cancellationToken, bool isAlreadyOwned = false) => Task.FromResult(_result);
            public Task<OcrInformation> DoOcrAsync(Image image) => Task.FromResult(_result);
        }

        [Fact]
        public async Task RecognizeAsync_NoProviders_ReturnsUnavailable()
        {
            var service = new EditorOcrService(() => Array.Empty<IOcrProvider>(), Path.GetTempPath());
            using var image = new Image<Rgba32>(4, 4);

            var outcome = await service.RecognizeAsync(image, null);

            Assert.Equal(EditorOcrStatus.Unavailable, outcome.Status);
            Assert.Null(outcome.Info);
        }

        [Fact]
        public async Task RecognizeAsync_ProviderReturnsNull_ReturnsUnavailable()
        {
            var service = new EditorOcrService(() => new IOcrProvider[] { new FakeProvider("a", null) }, Path.GetTempPath());
            using var image = new Image<Rgba32>(4, 4);

            var outcome = await service.RecognizeAsync(image, "a");

            Assert.Equal(EditorOcrStatus.Unavailable, outcome.Status);
        }

        [Fact]
        public async Task RecognizeAsync_ProviderResult_IsReturned()
        {
            var info = new OcrInformation { Text = "hello" };
            var service = new EditorOcrService(() => new IOcrProvider[] { new FakeProvider("a", info) }, Path.GetTempPath());
            using var image = new Image<Rgba32>(4, 4);

            var outcome = await service.RecognizeAsync(image, "a");

            Assert.Equal(EditorOcrStatus.Recognized, outcome.Status);
            Assert.Equal("hello", outcome.Info.Text);
        }

        [Fact]
        public async Task SaveTextBackupAsync_WritesTimestampedFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "FreeSnip_OcrBackupTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var service = new EditorOcrService(() => Array.Empty<IOcrProvider>(), dir);
                string path = await service.SaveTextBackupAsync("text", new DateTime(2026, 10, 24, 8, 17, 0, 5));

                Assert.Equal(Path.Combine(dir, "OCR_2026-10-24_08-17-00_005.txt"), path);
                Assert.Equal("text", File.ReadAllText(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
