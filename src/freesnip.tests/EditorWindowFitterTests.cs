#nullable enable
using Avalonia;
using freesnip.editor.Services;
using Xunit;

namespace freesnip.tests
{
    public class EditorWindowFitterTests
    {
        [Fact]
        public void CalculateInitialSize_ClampsToMinBounds()
        {
            var (w, h) = EditorWindowFitter.CalculateInitialSize(
                imageWidth: 100,
                imageHeight: 100,
                zoomFactor: 1.0,
                maxAllowedWidth: 1920,
                maxAllowedHeight: 1080);

            Assert.Equal(EditorWindowFitter.MinEditorWindowWidth, w);
            Assert.Equal(EditorWindowFitter.MinEditorWindowHeight, h);
        }

        [Fact]
        public void CalculateInitialSize_ClampsToMaxBounds()
        {
            var (w, h) = EditorWindowFitter.CalculateInitialSize(
                imageWidth: 5000,
                imageHeight: 3000,
                zoomFactor: 1.0,
                maxAllowedWidth: 1600,
                maxAllowedHeight: 900);

            Assert.Equal(1600, w);
            Assert.Equal(900, h);
        }

        [Fact]
        public void CalculateFitSize_AccountsForChromeAndZoom()
        {
            var (w, h) = EditorWindowFitter.CalculateFitSize(
                currentWidth: 1000,
                currentHeight: 800,
                viewportWidth: 800,
                viewportHeight: 600,
                imageWidth: 400,
                imageHeight: 300,
                zoomFactor: 2.0,
                maxAllowedWidth: 1920,
                maxAllowedHeight: 1080);

            Assert.Equal(1000, w);
            Assert.Equal(800, h);
        }

        [Fact]
        public void CalculateCenteredPosition_CentersCorrectly()
        {
            var workingArea = new PixelRect(0, 0, 1920, 1080);
            var pos = EditorWindowFitter.CalculateCenteredPosition(
                workingArea: workingArea,
                windowWidth: 1000,
                windowHeight: 600,
                scaling: 1.0,
                cascadeOffset: 0);

            Assert.Equal(460, pos.X);
            Assert.Equal(240, pos.Y);

            var cascadePos = EditorWindowFitter.CalculateCenteredPosition(
                workingArea: workingArea,
                windowWidth: 1000,
                windowHeight: 600,
                scaling: 1.0,
                cascadeOffset: 1);

            Assert.Equal(480, cascadePos.X);
            Assert.Equal(260, cascadePos.Y);
        }

        [Fact]
        public void TryFit_ReturnsFalseWhenAlreadyFitted()
        {
            var fitter = new EditorWindowFitter();
            fitter.Configure(new PixelRect(0, 0, 1920, 1080), 1.0, 1920, 1080);

            bool needsResize = fitter.TryFit(
                currentWidth: 1000,
                currentHeight: 800,
                viewportWidth: 800,
                viewportHeight: 600,
                imageWidth: 800,
                imageHeight: 600,
                zoomFactor: 1.0,
                out var newSize,
                out var newPos);

            Assert.False(needsResize);
        }

        [Fact]
        public void TryFit_ReturnsTrueWhenResizeNeeded()
        {
            var fitter = new EditorWindowFitter();
            fitter.Configure(new PixelRect(0, 0, 1920, 1080), 1.0, 1920, 1080);

            bool needsResize = fitter.TryFit(
                currentWidth: 1000,
                currentHeight: 800,
                viewportWidth: 800,
                viewportHeight: 600,
                imageWidth: 1200,
                imageHeight: 900,
                zoomFactor: 1.0,
                out var newSize,
                out var newPos);

            Assert.True(needsResize);
            Assert.Equal(1400, newSize.Width);
            Assert.Equal(1080, newSize.Height);
        }
    }
}

