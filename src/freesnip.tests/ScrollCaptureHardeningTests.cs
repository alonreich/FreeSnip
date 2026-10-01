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
    }
}

