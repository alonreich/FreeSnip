using System;
using Avalonia;
using freesnip.editor.Services;
using Xunit;

namespace freesnip.tests
{
    public class EditorZoomEngineTests
    {
        [Fact]
        public void CalculateLayout_FittingImage_IsCenteredInsideActualViewport()
        {
            var layout = EditorZoomEngine.CalculateLayout(1.0, 800, 400, 1000, 600, 1200, 800, null, null);

            Assert.Equal(100, layout.PadX, 3);
            Assert.Equal(100, layout.PadY, 3);
            Assert.Equal(1000, layout.ContentWidth, 3);
            Assert.Equal(600, layout.ContentHeight, 3);
            Assert.Equal(new Vector(0, 0), layout.Offset);
        }

        [Fact]
        public void CalculateLayout_ViewportShrunkByScrollbar_NeverLeavesImageEdgeOutsideViewport()
        {
            // Same window, but a non-auto-hide vertical scrollbar took 17px of viewport width.
            var wide = EditorZoomEngine.CalculateLayout(1.0, 990, 400, 1000, 600, 1200, 800, null, null);
            var narrow = EditorZoomEngine.CalculateLayout(1.0, 990, 400, 983, 600, 1200, 800, null, null);

            Assert.True(wide.PadX + wide.ScaledWidth <= 1000);
            Assert.True(narrow.ContentWidth >= narrow.PadX * 2 + narrow.ScaledWidth - 0.001);
            double visibleRight = narrow.Offset.X + 983;
            double imageCenter = narrow.PadX + narrow.ScaledWidth / 2;
            Assert.Equal(narrow.Offset.X + 983 / 2.0, imageCenter, 3);
            Assert.True(visibleRight <= narrow.ContentWidth + 0.001);
        }

        [Fact]
        public void CalculateLayout_UnmeasuredViewport_FallsBackToWindowEstimate()
        {
            var layout = EditorZoomEngine.CalculateLayout(1.0, 400, 300, 0, 0, 1060, 752, null, null);

            Assert.Equal((1000 - 400) / 2.0, layout.PadX, 3);
            Assert.Equal((600 - 300) / 2.0, layout.PadY, 3);
        }

        [Fact]
        public void ZoomFactor_ClampsToValidRange()
        {
            var engine = new EditorZoomEngine();

            engine.ZoomFactor = 0.01;
            Assert.Equal(EditorZoomEngine.MinZoomFactor, engine.ZoomFactor);

            engine.ZoomFactor = 10.0;
            Assert.Equal(EditorZoomEngine.MaxZoomFactor, engine.ZoomFactor);

            engine.ZoomFactor = 2.5;
            Assert.Equal(2.5, engine.ZoomFactor);
        }

        [Fact]
        public void StepInterpolation_AdvancesCorrectly()
        {
            bool step1 = EditorZoomEngine.StepInterpolation(1.0, 2.0, out double next1, out bool completed1);
            Assert.True(step1);
            Assert.False(completed1);
            Assert.Equal(1.20, next1, 2);

            bool step2 = EditorZoomEngine.StepInterpolation(1.9998, 2.0, out double next2, out bool completed2);
            Assert.True(step2);
            Assert.True(completed2);
            Assert.Equal(2.0, next2);
        }

        [Fact]
        public void CalculateViewportPadding_CentersSmallImages_ProvidesOverscrollForLarge()
        {
            EditorZoomEngine.CalculateViewportPadding(
                zoom: 1.0,
                imageWidth: 400,
                imageHeight: 300,
                viewportWidth: 800,
                viewportHeight: 600,
                windowWidth: 900,
                windowHeight: 700,
                out double padX,
                out double padY,
                out double scaledW,
                out double scaledH);

            Assert.Equal(400.0, scaledW);
            Assert.Equal(300.0, scaledH);
            Assert.Equal(200.0, padX);
            Assert.Equal(150.0, padY);

            EditorZoomEngine.CalculateViewportPadding(
                zoom: 1.0,
                imageWidth: 1000,
                imageHeight: 800,
                viewportWidth: 800,
                viewportHeight: 600,
                windowWidth: 900,
                windowHeight: 700,
                out padX,
                out padY,
                out scaledW,
                out scaledH);

            Assert.Equal(1000.0, scaledW);
            Assert.Equal(800.0, scaledH);
            Assert.Equal(EditorZoomEngine.OverscrollPadding, padX);
            Assert.Equal(EditorZoomEngine.OverscrollPadding, padY);
        }

        [Fact]
        public void CalculateFocalPoints_ClampsCorrectly()
        {
            EditorZoomEngine.CalculateFocalPoints(
                pointerInScroller: new Point(400, 300),
                scrollOffsetX: 0,
                scrollOffsetY: 0,
                zoom: 1.0,
                imageWidth: 500,
                imageHeight: 500,
                viewportWidth: 800,
                viewportHeight: 600,
                currentPadX: 100,
                currentPadY: 100,
                out Point vpPoint,
                out Point unscaledPoint);

            Assert.Equal(400.0, vpPoint.X);
            Assert.Equal(300.0, vpPoint.Y);
            Assert.Equal(300.0, unscaledPoint.X);
            Assert.Equal(200.0, unscaledPoint.Y);

            EditorZoomEngine.CalculateFocalPoints(
                pointerInScroller: new Point(400, 300),
                scrollOffsetX: 50,
                scrollOffsetY: 80,
                zoom: 1.0,
                imageWidth: 500,
                imageHeight: 500,
                viewportWidth: 800,
                viewportHeight: 600,
                currentPadX: 100,
                currentPadY: 100,
                out Point vpPoint2,
                out Point unscaledPoint2);

            Assert.Equal(400.0, vpPoint2.X);
            Assert.Equal(300.0, vpPoint2.Y);
            Assert.Equal(350.0, unscaledPoint2.X);
            Assert.Equal(280.0, unscaledPoint2.Y);
        }

        [Fact]
        public void CalculateDesiredOffset_FitsInViewportZeroOffset()
        {
            var offset = EditorZoomEngine.CalculateDesiredOffset(
                zoom: 1.0,
                padX: 200,
                padY: 150,
                totalW: 800,
                totalH: 600,
                viewportWidth: 800,
                viewportHeight: 600,
                focalVp: null,
                focalUnscaled: null);

            Assert.Equal(0.0, offset.X);
            Assert.Equal(0.0, offset.Y);
        }
    }
}

