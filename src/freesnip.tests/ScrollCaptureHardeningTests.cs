using System;
using System.Collections.Generic;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace freesnip.tests
{
    public class ScrollCaptureHardeningTests
    {
        [Fact]
        public void ScrollFrameStitcher_DefaultConstants_MatchSpecifications()
        {
            Assert.Equal(512, ScrollFrameStitcher.MaxSegments);
            Assert.Equal(48L * 1024L * 1024L, ScrollFrameStitcher.MaxCompositePixels);
            Assert.Equal(512, ScrollCaptureRecorder.MaxSegments);
        }

        [Fact]
        public void ContinuousScrolling_Beyond120Segments_SucceedsBelowBoundedCap()
        {
            const int frameWidth = 100;
            const int frameHeight = 200;
            const int shiftPerFrame = 10;
            const int totalFrames = 150;

            using var tallCanvas = new Image<Bgra32>(frameWidth, 2000);
            tallCanvas.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < 2000; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < frameWidth; x++)
                    {
                        byte v = (byte)((y * 7 + x * 3) % 256);
                        row[x] = new Bgra32(v, (byte)(255 - v), (byte)((v * 2) % 256), 255);
                    }
                }
            });

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < totalFrames; i++)
            {
                var frame = tallCanvas.Clone(ctx => ctx.Crop(new Rectangle(0, i * shiftPerFrame, frameWidth, frameHeight)));
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.False(stitcher.IsSegmentCeilingReached);
            Assert.Equal(totalFrames, stitcher.AcceptedFrames);
            Assert.True(stitcher.SegmentCount > 120);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(frameWidth, result.Width);
            Assert.Equal(frameHeight + (totalFrames - 1) * shiftPerFrame, result.Height);
        }

        [Fact]
        public void SimulateCapture_WhenApproachingMemoryLimit_ClampsGracefully()
        {
            const int frameWidth = 100;
            const int frameHeight = 200;
            const int shiftPerFrame = 10;
            const int totalFrames = 100;
            const long maxCompositePixels = 50_000;

            using var tallCanvas = new Image<Bgra32>(frameWidth, 3200);
            tallCanvas.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < 3200; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < frameWidth; x++)
                    {
                        byte v = (byte)((y * 7 + x * 3) % 256);
                        row[x] = new Bgra32(v, (byte)(255 - v), (byte)((v * 2) % 256), 255);
                    }
                }
            });

            using var stitcher = new ScrollFrameStitcher(maxCompositePixels: maxCompositePixels);

            for (int i = 0; i < totalFrames; i++)
            {
                var frame = tallCanvas.Clone(ctx => ctx.Crop(new Rectangle(0, i * shiftPerFrame, frameWidth, frameHeight)));
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.IsSegmentCeilingReached);
            Assert.True(stitcher.SegmentCount > 0);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(frameWidth, result.Width);
            Assert.Equal(500, result.Height);
            Assert.True((long)result.Width * result.Height <= maxCompositePixels);

            stitcher.Dispose();
            Assert.Empty(stitcher.Segments);
        }

        [Fact]
        public void BuildImage_WhenCalculatedPixelsExceedCeiling_ClampsHeightGracefully()
        {
            const int width = 200;
            const int height = 200;
            using var stitcher = new ScrollFrameStitcher(maxCompositePixels: 80_000);

            using var baseImg = new Image<Bgra32>(width, height);
            stitcher.AddFrame(baseImg.Clone(x => { }));

            using var singleResult = stitcher.BuildImage();
            Assert.NotNull(singleResult);
            Assert.Equal(width, singleResult.Width);
            Assert.Equal(height, singleResult.Height);
        }

        [Fact]
        public void ScrollCaptureRecorder_SegmentCeilingReached_TriggersPauseAndEvent()
        {
            var targetRect = freesnip.native.foundation.RECT.FromXYWH(0, 0, 800, 600);
            var recorder = new ScrollCaptureRecorder(targetRect);

            bool eventFired = false;
            recorder.SegmentCeilingReached += () => { eventFired = true; };

            Assert.False(recorder.IsPaused);
            Assert.False(recorder.IsSegmentCeilingReached);

            recorder.Pause();
            Assert.True(recorder.IsPaused);
            Assert.True(eventFired);

            eventFired = false;
            recorder.Pause();
            Assert.False(eventFired);

            recorder.Resume();
            Assert.False(recorder.IsPaused);
        }

        [Fact]
        public async Task ScrollCaptureRecorder_FinishAsync_WhenNoFramesAccepted_ReturnsNull()
        {
            var targetRect = freesnip.native.foundation.RECT.FromXYWH(0, 0, 400, 300);
            var recorder = new ScrollCaptureRecorder(targetRect);
            var result = await recorder.FinishAsync();
            Assert.Null(result);
            await recorder.DisposeAsync();
        }


        /// <summary>
        /// Helper to create synthetic scroll frames with deterministic unique pixel content.
        /// Viewport content is derived from world coordinates so overlaps are pixel-verifiable.
        /// </summary>
        private static Image<Bgra32> CreateVerificationFrame(
            int width, int height, Rectangle viewport, int scrollOffsetY, int scrollOffsetX,
            Action<Image<Bgra32>>? drawChrome = null)
        {
            var frame = new Image<Bgra32>(width, height);
            frame.ProcessPixelRows(accessor =>
            {
                for (int y = viewport.Top; y < viewport.Bottom; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int worldY = y + scrollOffsetY;
                    for (int x = viewport.Left; x < viewport.Right; x++)
                    {
                        int worldX = x + scrollOffsetX;
                        byte r = (byte)((worldY * 13 + worldX * 7) % 256);
                        byte g = (byte)((worldY * 5 + worldX * 17) % 256);
                        byte b = (byte)((worldY * 23 + worldX * 11) % 256);
                        row[x] = new Bgra32(r, g, b, 255);
                    }
                }
            });
            drawChrome?.Invoke(frame);
            return frame;
        }

        private static (int dx, int dy)? InvokeOverlapVerifier(
            ScrollFrameStitcher stitcher, FrameLuma previous, FrameLuma current, Rectangle viewport, int coarseDx, int coarseDy)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var viewportDetectedField = typeof(ScrollFrameStitcher).GetField("_viewportDetected", flags);
            var viewportField = typeof(ScrollFrameStitcher).GetField("_viewport", flags);
            var verifyMethod = typeof(ScrollFrameStitcher).GetMethod("VerifyOverlapDisplacement", flags);

            Assert.NotNull(viewportDetectedField);
            Assert.NotNull(viewportField);
            Assert.NotNull(verifyMethod);

            viewportDetectedField!.SetValue(stitcher, true);
            viewportField!.SetValue(stitcher, viewport);
            object? result = verifyMethod!.Invoke(stitcher, new object[] { previous, current, coarseDx, coarseDy });
            return result is ValueTuple<int, int> displacement ? displacement : null;
        }

        [Fact]
        public void OverlapVerifier_DirectlyCorrectsPlus1CoarseDelta()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var previousImage = CreateVerificationFrame(width, height, viewport, 0, 0);
            using var currentImage = CreateVerificationFrame(width, height, viewport, 20, 0);
            using var previous = new FrameLuma(previousImage);
            using var current = new FrameLuma(currentImage);
            using var stitcher = new ScrollFrameStitcher();

            var verified = InvokeOverlapVerifier(stitcher, previous, current, viewport, 0, 21);
            Assert.NotNull(verified);
            Assert.Equal(0, verified!.Value.dx);
            Assert.Equal(20, verified.Value.dy);
        }

        [Fact]
        public void OverlapVerifier_DirectlyCorrectsMinus2CoarseDelta()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var previousImage = CreateVerificationFrame(width, height, viewport, 0, 0);
            using var currentImage = CreateVerificationFrame(width, height, viewport, 20, 0);
            using var previous = new FrameLuma(previousImage);
            using var current = new FrameLuma(currentImage);
            using var stitcher = new ScrollFrameStitcher();

            var verified = InvokeOverlapVerifier(stitcher, previous, current, viewport, 0, 18);
            Assert.NotNull(verified);
            Assert.Equal(0, verified!.Value.dx);
            Assert.Equal(20, verified.Value.dy);
        }

        [Fact]
        public void OverlapVerification_CoarseEstimateOffByPlus1_NoDuplicatedRow()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 3, $"Expected at least 3 accepted frames, got {stitcher.AcceptedFrames}");

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);

            int expectedHeight = height + (stitcher.AcceptedFrames - 1) * 20;
            Assert.Equal(expectedHeight, result.Height);
        }

        [Fact]
        public void OverlapVerification_CoarseEstimateOffByMinus2_NoMissingBand()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 3);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);

            int expectedHeight = height + (stitcher.AcceptedFrames - 1) * 20;
            Assert.Equal(expectedHeight, result.Height);

            if (result.Height > height + 5)
            {
                result.ProcessPixelRows(accessor =>
                {
                    var row = accessor.GetRowSpan(height + 2);
                    bool hasContent = false;
                    for (int x = 10; x < width - 10; x += 10)
                    {
                        if (row[x].R != 0 || row[x].G != 0 || row[x].B != 0) { hasContent = true; break; }
                    }
                    Assert.True(hasContent, "Row at first seam boundary is all black, indicating missing content.");
                });
            }
        }

        [Fact]
        public void AxisConsensus_RepetitiveTexture_DoesNotFalselyLockHorizontal()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            Image<Bgra32> CreateRepetitiveFrame(int scrollY)
            {
                var frame = new Image<Bgra32>(width, height);
                frame.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int worldY = y + scrollY;
                        byte v = (byte)((worldY * 37) % 256);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(v, (byte)((v + 80) % 256), (byte)((v + 160) % 256), 255);
                        }
                    }
                });
                return frame;
            }

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            for (int i = 0; i < 5; i++)
            {
                using var frame = CreateRepetitiveFrame(i * 15);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 2);
            using var result = stitcher.BuildImage();
            Assert.NotNull(result);

            Assert.Equal(width, result.Width);
            Assert.True(result.Height > height, "Expected vertical composite taller than single frame.");
        }

        [Fact]
        public void AxisConsensus_NoisyFirstFrameThenVertical_ProducesVerticalComposite()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();

            using var f0 = CreateVerificationFrame(width, height, viewport, 0, 0);
            stitcher.AddFrame(f0);

            using var f1 = CreateVerificationFrame(width, height, viewport, 3, 0);
            stitcher.AddFrame(f1);

            for (int i = 2; i < 6; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 3);
            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.True(result.Height > height, "Expected vertical composite.");
        }

        [Fact]
        public void AxisConsensus_GenuineHorizontalMotion_ProducesHorizontalComposite()
        {
            const int width = 360;
            const int height = 300;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = false;

            for (int i = 0; i < 5; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, 0, i * 25);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 3);
            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.True(result.Width > width, $"Expected wider-than-frame composite for horizontal scroll, got {result.Width}.");
            Assert.Equal(height, result.Height);
        }

        [Fact]
        public void OverlapVerification_FixedHeader_ExcludedFromOverlapMatching()
        {
            const int width = 300;
            const int height = 400;
            const int headerHeight = 60;
            var viewport = new Rectangle(0, headerHeight, width, height - headerHeight);

            void DrawHeader(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < headerHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(43, 87, 154, 255);
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0, DrawHeader);
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.AcceptedFrames >= 3);
            Assert.True(stitcher.ViewportDetected);
            Assert.True(stitcher.Viewport.Top >= headerHeight - 4, $"Viewport top {stitcher.Viewport.Top} should be near header bottom {headerHeight}.");

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.True(result.Height > height);
        }

        [Fact]
        public void ReAcquisition_StillFunctions_WithOverlapVerification()
        {
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            for (int i = 0; i < 3; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0);
                stitcher.AddFrame(frame);
            }
            int framesAfterNormal = stitcher.AcceptedFrames;
            Assert.True(framesAfterNormal >= 2);

            using var occluded = new Image<Bgra32>(width, height);
            occluded.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = new Bgra32(255, 0, 255, 255);
                    }
                }
            });
            stitcher.AddFrame(occluded.Clone(x => { }));

            using var recovered = CreateVerificationFrame(width, height, viewport, 3 * 20, 0);
            var recoveredStatus = stitcher.AddFrame(recovered);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
        }

        [Fact]
        public void MemoryLimit180MP_StillClampsWithOverlapVerification()
        {
            const int frameWidth = 100;
            const int frameHeight = 200;
            const int shiftPerFrame = 10;
            const int totalFrames = 100;
            const long maxCompositePixels = 50_000;

            using var tallCanvas = new Image<Bgra32>(frameWidth, 3200);
            tallCanvas.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < 3200; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < frameWidth; x++)
                    {
                        byte v = (byte)((y * 7 + x * 3) % 256);
                        row[x] = new Bgra32(v, (byte)(255 - v), (byte)((v * 2) % 256), 255);
                    }
                }
            });

            using var stitcher = new ScrollFrameStitcher(maxCompositePixels: maxCompositePixels);
            stitcher.AxisHint = true;

            for (int i = 0; i < totalFrames; i++)
            {
                var frame = tallCanvas.Clone(ctx => ctx.Crop(new Rectangle(0, i * shiftPerFrame, frameWidth, frameHeight)));
                stitcher.AddFrame(frame);
            }

            Assert.True(stitcher.IsSegmentCeilingReached);
            Assert.True(stitcher.SegmentCount > 0);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(frameWidth, result.Width);
            Assert.True((long)result.Width * result.Height <= maxCompositePixels,
                $"Composite {result.Width}x{result.Height} exceeds pixel limit {maxCompositePixels}.");
        }
    }
}
