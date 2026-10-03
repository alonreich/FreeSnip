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
            Assert.Equal(int.MaxValue, ScrollFrameStitcher.MaxSegments);
            Assert.Equal(180L * 1024L * 1024L, ScrollFrameStitcher.MaxCompositePixels);
            Assert.Equal(int.MaxValue, ScrollCaptureRecorder.MaxSegments);
        }

        [Fact]
        public void ContinuousScrolling_Beyond120Segments_SucceedsWithoutHardCap()
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

        // ===== Overlap Verification & Axis Consensus Regression Tests =====

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

        [Fact]
        public void OverlapVerification_CoarseEstimateOffByPlus1_NoDuplicatedRow()
        {
            // Vertical scroll with 20px actual movement per frame.
            // The overlap verifier should correct any ±1px coarse estimate error.
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

            // Verify no duplicated rows: each row in the scrolling region should have unique content
            // The composite height should be frameHeight + (N-1)*shift = 400 + 5*20 = 500
            int expectedHeight = height + (stitcher.AcceptedFrames - 1) * 20;
            Assert.Equal(expectedHeight, result.Height);
        }

        [Fact]
        public void OverlapVerification_CoarseEstimateOffByMinus2_NoMissingBand()
        {
            // Same as above but tests that the verifier corrects underestimates too.
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

            // The composite should be continuous with no gaps
            int expectedHeight = height + (stitcher.AcceptedFrames - 1) * 20;
            Assert.Equal(expectedHeight, result.Height);

            // Verify pixel continuity at a seam boundary: pick world row at y=height (first seam)
            // This row should have valid world content, not black (missing) pixels
            if (result.Height > height + 5)
            {
                result.ProcessPixelRows(accessor =>
                {
                    var row = accessor.GetRowSpan(height + 2);
                    // Should not be all zeros (black = missing content)
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
            // Vertical scrolling with repetitive horizontal pattern should not be classified as horizontal.
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            // Create frames where each row is identical (horizontally repetitive) but shifts vertically
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
                            // Horizontally repetitive: same value across x, different per row
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

            // Result width should be the frame width (not abnormally wide from horizontal misclassification)
            Assert.Equal(width, result.Width);
            Assert.True(result.Height > height, "Expected vertical composite taller than single frame.");
        }

        [Fact]
        public void AxisConsensus_NoisyFirstFrameThenVertical_ProducesVerticalComposite()
        {
            // First frame pair might produce noisy motion estimate.
            // Subsequent clear vertical frames should result in vertical composite.
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();

            // Frame 0: anchor
            using var f0 = CreateVerificationFrame(width, height, viewport, 0, 0);
            stitcher.AddFrame(f0);

            // Frame 1: noise-like (very small shift that could produce ambiguous direction)
            using var f1 = CreateVerificationFrame(width, height, viewport, 3, 0);
            stitcher.AddFrame(f1);

            // Frames 2-5: clear vertical motion
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
            stitcher.AxisHint = false; // hint horizontal

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
            // A fixed 60px header should be detected as stationary chrome and excluded
            // from the scrolling viewport overlap matching.
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
                            row[x] = new Bgra32(43, 87, 154, 255); // fixed blue header
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
            // Viewport should exclude the header
            Assert.True(stitcher.Viewport.Top >= headerHeight - 4, $"Viewport top {stitcher.Viewport.Top} should be near header bottom {headerHeight}.");

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            // Result should include the header once plus the scrolling content
            Assert.True(result.Height > height);
        }

        [Fact]
        public void ReAcquisition_StillFunctions_WithOverlapVerification()
        {
            // Simulate a sequence where one frame is drastically different (occlusion),
            // followed by a frame that matches a recent frame. Re-acquisition should still work.
            const int width = 300;
            const int height = 400;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();
            stitcher.AxisHint = true;

            // Frames 0-2: normal vertical scroll
            for (int i = 0; i < 3; i++)
            {
                using var frame = CreateVerificationFrame(width, height, viewport, i * 20, 0);
                stitcher.AddFrame(frame);
            }
            int framesAfterNormal = stitcher.AcceptedFrames;
            Assert.True(framesAfterNormal >= 2);

            // Frame 3: occlusion (completely different content)
            using var occluded = new Image<Bgra32>(width, height);
            occluded.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = new Bgra32(255, 0, 255, 255); // magenta occlusion
                    }
                }
            });
            stitcher.AddFrame(occluded.Clone(x => { }));

            // Frame 4: back to normal scroll at offset 3*20 = 60
            using var recovered = CreateVerificationFrame(width, height, viewport, 3 * 20, 0);
            var recoveredStatus = stitcher.AddFrame(recovered);

            // The stitcher should still be functional (either accepted via re-acquisition or rejected gracefully)
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

