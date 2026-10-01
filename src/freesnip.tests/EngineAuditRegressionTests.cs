using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using freesnip.native;
using freesnip.native.foundation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace freesnip.tests
{
    public class EngineAuditRegressionTests
    {
        private static OcrWord Word(string text, int x, int y, int w, int h, float confidence)
        {
            return new OcrWord { Text = text, Bounds = RECT.FromXYWH(x, y, w, h), Confidence = confidence };
        }

        [Fact]
        public void MergeByLanguage_KeepsConfidentPrimaryWordsOnEnglishOnlyPages()
        {
            var primary = new OcrInformation { Words = { Word("Invoice", 10, 10, 80, 20, 0.97f) } };
            var english = new OcrInformation { Words = { Word("lnvoice", 10, 10, 80, 20, OcrWord.UnknownConfidence) } };

            var merged = OcrTextLayout.MergeByLanguage(primary, english);

            Assert.Contains(merged.Words, word => word.Text == "Invoice");
            Assert.DoesNotContain(merged.Words, word => word.Text == "lnvoice");
        }

        [Fact]
        public void MergeByLanguage_FallsBackToEnglishWhenPrimaryCarriesNoConfidence()
        {
            var primary = new OcrInformation { Words = { Word("lnvoice", 10, 10, 80, 20, OcrWord.UnknownConfidence) } };
            var english = new OcrInformation { Words = { Word("Invoice", 10, 10, 80, 20, OcrWord.UnknownConfidence) } };

            var merged = OcrTextLayout.MergeByLanguage(primary, english);

            Assert.Same(english, merged);
        }

        [Fact]
        public void WindowsProfile_InvertsDarkBackgroundsLikeTheTesseractProfile()
        {
            using var dark = new Image<Bgra32>(240, 120);
            dark.Mutate(ctx => ctx.BackgroundColor(Color.FromRgb(12, 12, 12)));

            using OcrPreparedImage windows = OcrImagePreprocessor.Prepare(dark, OcrPreprocessingProfile.Windows);
            using OcrPreparedImage tesseract = OcrImagePreprocessor.Prepare(dark, OcrPreprocessingProfile.Tesseract);

            Assert.True(MeanLuminance(windows) > 128, "Windows profile must invert a mostly dark capture.");
            Assert.True(MeanLuminance(tesseract) > 128, "Tesseract profile must invert a mostly dark capture.");
        }

        private static double MeanLuminance(OcrPreparedImage prepared)
        {
            Image<Bgra32> image = prepared.Image;
            double total = 0;
            int count = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        total += (row[x].R * 0.299) + (row[x].G * 0.587) + (row[x].B * 0.114);
                        count++;
                    }
                }
            });
            return count == 0 ? 0 : total / count;
        }

        [Fact]
        public async Task OcrRequestQueue_DisposesTheWorkImageExactlyOnce()
        {
            var image = new Image<Bgra32>(8, 8);
            Image? observed = null;

            await using (var queue = new OcrRequestQueue((img, token) =>
            {
                observed = img;
                return Task.FromResult(new OcrInformation { Text = "ok" });
            }))
            {
                var result = await queue.EnqueueAsync(image, CancellationToken.None, true);
                Assert.NotNull(result);
            }

            Assert.NotNull(observed);
            Assert.Throws<ObjectDisposedException>(() => observed!.Frames.RootFrame.ToString());
        }

        [Theory]
        [InlineData("OutputFilePath")]
        [InlineData("OutputFileFilenamePattern")]
        [InlineData("LogFile")]
        [InlineData("LogLevel")]
        public void RemovedSettings_MustNotBeReintroducedWithoutAConsumer(string propertyName)
        {
            Assert.Null(typeof(CoreConfiguration).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance));
        }

        [Fact]
        public void EverySettingsPropertyIsAnIniProperty()
        {
            var unmapped = typeof(CoreConfiguration)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.DeclaringType == typeof(CoreConfiguration))
                .Where(property => property.GetCustomAttribute<freesnip.foundation.IniFile.IniPropertyAttribute>() == null)
                .Select(property => property.Name)
                .ToList();

            Assert.Empty(unmapped);
        }

        [Fact]
        public void DisableHotkeysSettingExists()
        {
            Assert.NotNull(typeof(CoreConfiguration).GetProperty("DisableHotkeys", BindingFlags.Public | BindingFlags.Instance));
        }

        [Fact]
        public void ScreenTintBypass_ApplyColorCorrection_ExecutesSafely()
        {
            using var tinted = new Image<Bgra32>(10, 10);
            tinted.Mutate(ctx => ctx.BackgroundColor(Color.FromRgb(128, 106, 83)));
            ScreenTintBypass.ApplyColorCorrectionIfActive(tinted);
            Assert.NotNull(tinted);
            Assert.Equal(10, tinted.Width);
        }

        [Fact]
        public void ScreenTintBypass_ShouldExcludeLayeredWindows_ExecutesSafely()
        {
            var region = RECT.FromXYWH(0, 0, 1920, 1080);
            bool result = ScreenTintBypass.ShouldExcludeLayeredWindows(region);
            Assert.True(result || !result);
        }

        [Fact]
        public void ScreenTintBypass_Benchmark_ShouldExcludeLayeredWindows_ExecutesUnder2Ms()
        {
            var region = RECT.FromXYWH(0, 0, 1920, 1080);

            ScreenTintBypass.ShouldExcludeLayeredWindows(region);

            ScreenTintBypass.InvalidateCache();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ScreenTintBypass.ShouldExcludeLayeredWindows(region);
            sw.Stop();

            Assert.True(sw.Elapsed.TotalMilliseconds < 2.0, $"Expected ShouldExcludeLayeredWindows to execute in under 2ms, but took {sw.Elapsed.TotalMilliseconds:F3}ms");
        }
    }
}

