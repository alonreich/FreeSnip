using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using freesnip.editor.Services;
using freesnip.foundation.interfaces.Ocr;
using Xunit;

namespace freesnip.tests
{
    public class OcrOverlayControllerTests
    {
        [Fact]
        public void SampleUnderlyingLuminance_WhiteImage_ReturnsHighLuminance()
        {
            using var whiteImg = new Image<Rgba32>(100, 100, new Rgba32(255, 255, 255));
            double lum = OcrOverlayController.SampleUnderlyingLuminance(whiteImg, 10, 10, 50, 50);

            Assert.True(lum > 0.95);
        }

        [Fact]
        public void SampleUnderlyingLuminance_BlackImage_ReturnsLowLuminance()
        {
            using var blackImg = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0));
            double lum = OcrOverlayController.SampleUnderlyingLuminance(blackImg, 10, 10, 50, 50);

            Assert.True(lum < 0.05);
        }

        [Fact]
        public void SampleUnderlyingLuminance_NullImage_ReturnsDefaultLuminance()
        {
            double lum = OcrOverlayController.SampleUnderlyingLuminance(null, 10, 10, 50, 50);
            Assert.Equal(0.2, lum);
        }

        [Fact]
        public void CalculateToolbarPosition_NormalPosition_PlacesRight()
        {
            OcrOverlayController.CalculateToolbarPosition(
                wordX: 100,
                wordY: 100,
                wordW: 50,
                wordH: 20,
                tbW: 100,
                tbH: 30,
                maxW: 500,
                maxH: 500,
                out double tbX,
                out double tbY);

            Assert.Equal(156, tbX);
            Assert.Equal(100, tbY);
        }

        [Fact]
        public void CalculateToolbarPosition_OverflowRight_PlacesLeft()
        {
            OcrOverlayController.CalculateToolbarPosition(
                wordX: 450,
                wordY: 100,
                wordW: 40,
                wordH: 20,
                tbW: 100,
                tbH: 30,
                maxW: 500,
                maxH: 500,
                out double tbX,
                out double tbY);

            Assert.Equal(344, tbX);
            Assert.Equal(100, tbY);
        }

        [Fact]
        public void DetectActions_IdentifiesUrlsAndEmails()
        {
            var (urlLink, urlEmail) = OcrOverlayController.DetectActions("https://example.com/test");
            Assert.True(urlLink);
            Assert.False(urlEmail);

            var (emailLink, emailEmail) = OcrOverlayController.DetectActions("user@example.org");
            Assert.False(emailLink);
            Assert.True(emailEmail);

            var (textLink, textEmail) = OcrOverlayController.DetectActions("just normal text");
            Assert.False(textLink);
            Assert.False(textEmail);
        }

        [Fact]
        public void CollectSelectedWords_GathersRange()
        {
            var controller = new OcrOverlayController();
            var word1 = new OcrWord { Text = "Hello", Bounds = freesnip.native.foundation.RECT.FromXYWH(0, 0, 40, 15) };
            var word2 = new OcrWord { Text = "World", Bounds = freesnip.native.foundation.RECT.FromXYWH(50, 0, 40, 15) };
            var word3 = new OcrWord { Text = "Test", Bounds = freesnip.native.foundation.RECT.FromXYWH(100, 0, 40, 15) };

            var info = new OcrInformation
            {
                Words = new List<OcrWord> { word1, word2, word3 },
                Text = "Hello World Test"
            };

            controller.SetOcrInfo(info);
            controller.SelectionStartIndex = 0;
            controller.SelectionEndIndex = 1;

            controller.CollectSelectedWords();

            Assert.Equal(2, controller.SelectedOcrWords.Count);
            Assert.Same(word1, controller.SelectedOcrWords[0]);
            Assert.Same(word2, controller.SelectedOcrWords[1]);
        }

        [Fact]
        public void ShowContextToolbar_WithSelectedWords_PositionsAndShowsToolbar()
        {
            var controller = new OcrOverlayController();
            var word = new OcrWord { Text = "Hello", Bounds = freesnip.native.foundation.RECT.FromXYWH(100, 100, 50, 20) };
            var info = new OcrInformation
            {
                Words = new List<OcrWord> { word },
                Text = "Hello"
            };

            controller.SetOcrInfo(info);
            controller.SelectionStartIndex = 0;
            controller.SelectionEndIndex = 0;
            controller.CollectSelectedWords();

            var toolbar = new Border { IsVisible = false };
            double appliedLum = -1;
            string? buttonText = null;

            controller.ShowContextToolbar(
                toolbar,
                maxW: 800,
                maxH: 600,
                sampleLuminance: (x, y, w, h) => 0.75,
                applyTheme: (tb, lum) => appliedLum = lum,
                updateToolbarButtons: text => buttonText = text);

            Assert.True(toolbar.IsVisible);
            Assert.Equal(0.75, appliedLum);
            Assert.Equal("Hello", buttonText);
            Assert.True(Canvas.GetLeft(toolbar) > 0);
            Assert.True(Canvas.GetTop(toolbar) >= 0);
        }

        [Fact]
        public void ShowContextToolbar_WithNoSelectedWords_HidesToolbar()
        {
            var controller = new OcrOverlayController();
            var toolbar = new Border { IsVisible = true };

            controller.ShowContextToolbar(toolbar, maxW: 800, maxH: 600);

            Assert.False(toolbar.IsVisible);
        }

        [Fact]
        public void HideContextToolbar_SetsIsVisibleFalse()
        {
            var controller = new OcrOverlayController();
            var toolbar = new Border { IsVisible = true };

            controller.HideContextToolbar(toolbar);
            Assert.False(toolbar.IsVisible);

            controller.HideContextToolbar(null);
        }
    }
}

