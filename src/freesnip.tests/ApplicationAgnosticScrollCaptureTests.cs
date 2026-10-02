using System;
using freesnip.helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace freesnip.tests
{
    public class ApplicationAgnosticScrollCaptureTests
    {
        private static Image<Bgra32> CreateSyntheticFrame(
            int width,
            int height,
            Rectangle viewport,
            int scrollOffsetY,
            int scrollOffsetX,
            Action<Image<Bgra32>>? drawStationaryChrome)
        {
            var frame = new Image<Bgra32>(width, height);

            // Draw scrolling pattern inside viewport
            frame.ProcessPixelRows(accessor =>
            {
                for (int y = viewport.Top; y < viewport.Bottom; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int worldY = y + scrollOffsetY;
                    for (int x = viewport.Left; x < viewport.Right; x++)
                    {
                        int worldX = x + scrollOffsetX;
                        // High-contrast textured pattern
                        byte r = (byte)((worldY * 13 + worldX * 7) % 256);
                        byte g = (byte)((worldY * 5 + worldX * 17) % 256);
                        byte b = (byte)((worldY * 23 + worldX * 11) % 256);
                        row[x] = new Bgra32(r, g, b, 255);
                    }
                }
            });

            // Draw stationary chrome (headers, footers, sidebars)
            drawStationaryChrome?.Invoke(frame);

            return frame;
        }

        [Fact]
        public void ScenarioA_WordLike_FixedRibbonHeader_FixedStatusBarFooter_VerticalScroll()
        {
            const int width = 300;
            const int height = 400;
            const int ribbonHeight = 60;
            const int statusHeight = 30;
            var viewport = new Rectangle(0, ribbonHeight, width, height - ribbonHeight - statusHeight);

            void DrawWordChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    // Fixed Ribbon (top 60px)
                    for (int y = 0; y < ribbonHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(43, 87, 154, 255); // Word blue
                        }
                    }
                    // Fixed Status bar (bottom 30px)
                    for (int y = height - statusHeight; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(230, 230, 230, 255); // Status gray
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            // Feed 8 frames scrolling vertically by 15px each
            for (int i = 0; i < 8; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 15, 0, DrawWordChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(8, stitcher.AcceptedFrames);
            Assert.True(stitcher.SegmentCount >= 7);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            // Result height = ribbonHeight + statusHeight + initial viewport + 7 * 15px
            int expectedHeight = height + 7 * 15;
            Assert.Equal(expectedHeight, result.Height);
        }

        [Fact]
        public void ScenarioB_ExcelLike_FixedRibbon_FixedRowHeader_VerticalScroll()
        {
            const int width = 320;
            const int height = 360;
            const int ribbonHeight = 50;
            const int rowHeaderWidth = 45;
            var viewport = new Rectangle(rowHeaderWidth, ribbonHeight, width - rowHeaderWidth, height - ribbonHeight);

            void DrawExcelChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    // Ribbon (top 50px)
                    for (int y = 0; y < ribbonHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(16, 124, 65, 255); // Excel green
                        }
                    }
                    // Fixed row header on left (below ribbon)
                    for (int y = ribbonHeight; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < rowHeaderWidth; x++)
                        {
                            row[x] = new Bgra32(240, 240, 240, 255); // Row header gray
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 20, 0, DrawExcelChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(6, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.Equal(height + 5 * 20, result.Height);
        }

        [Fact]
        public void ScenarioC_ExcelLike_HorizontalScroll_FixedHeaders()
        {
            const int width = 360;
            const int height = 300;
            const int ribbonHeight = 50;
            const int rowHeaderWidth = 50;
            var viewport = new Rectangle(rowHeaderWidth, ribbonHeight, width - rowHeaderWidth, height - ribbonHeight);

            void DrawExcelChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < ribbonHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(16, 124, 65, 255);
                        }
                    }
                    for (int y = ribbonHeight; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < rowHeaderWidth; x++)
                        {
                            row[x] = new Bgra32(240, 240, 240, 255);
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < 5; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, 0, i * 25, DrawExcelChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(5, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width + 4 * 25, result.Width);
            Assert.Equal(height, result.Height);
        }

        [Fact]
        public void ScenarioD_BrowserLike_FixedTopChrome_ScrollingContent()
        {
            const int width = 350;
            const int height = 400;
            const int chromeHeight = 70;
            var viewport = new Rectangle(0, chromeHeight, width, height - chromeHeight);

            void DrawBrowserChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < chromeHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(220, 220, 220, 255);
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 18, 0, DrawBrowserChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(6, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.Equal(height + 5 * 18, result.Height);
        }

        [Fact]
        public void ScenarioE_VSCode_FixedSidebar_ScrollingEditor()
        {
            const int width = 350;
            const int height = 350;
            const int sidebarWidth = 70;
            var viewport = new Rectangle(sidebarWidth, 0, width - sidebarWidth, height);

            void DrawVSCodeChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < sidebarWidth; x++)
                        {
                            row[x] = new Bgra32(37, 37, 38, 255); // VS Code dark sidebar
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < 6; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 16, 0, DrawVSCodeChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(6, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.Equal(height + 5 * 16, result.Height);
        }

        [Fact]
        public void ScenarioF_SmallWheelIncrements()
        {
            const int width = 250;
            const int height = 300;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();

            // Small 4px increments
            for (int i = 0; i < 10; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 4, 0, null);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(10, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.Equal(height + 9 * 4, result.Height);
        }

        [Fact]
        public void ScenarioG_BlankMargins_SurroundingContent_TracksContent()
        {
            const int width = 350;
            const int height = 350;
            const int margin = 50;
            // Center content with blank white margins around it
            var viewport = new Rectangle(margin, 0, width - 2 * margin, height);

            void DrawBlankMargins(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < margin; x++)
                        {
                            row[x] = new Bgra32(255, 255, 255, 255); // Blank left
                        }
                        for (int x = width - margin; x < width; x++)
                        {
                            row[x] = new Bgra32(255, 255, 255, 255); // Blank right
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            for (int i = 0; i < 5; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 15, 0, DrawBlankMargins);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(5, stitcher.AcceptedFrames);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
        }

        [Fact]
        public void ScenarioH_ReacquisitionAfterTrackingLoss()
        {
            const int width = 250;
            const int height = 300;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();

            // Frame 0: anchor
            using var frame0 = CreateSyntheticFrame(width, height, viewport, 0, 0, null);
            Assert.Equal(ScrollFrameStatus.Accepted, stitcher.AddFrame(frame0));

            // Frame 1: shifted by 15px
            using var frame1 = CreateSyntheticFrame(width, height, viewport, 15, 0, null);
            Assert.Equal(ScrollFrameStatus.Accepted, stitcher.AddFrame(frame1));

            // Frame 2: corrupted/random glitch frame -> should be rejected
            using var glitchFrame = new Image<Bgra32>(width, height);
            glitchFrame.ProcessPixelRows(accessor =>
            {
                var rand = new Random(12345);
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = new Bgra32((byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256), 255);
                    }
                }
            });
            var glitchStatus = stitcher.AddFrame(glitchFrame);
            Assert.Equal(ScrollFrameStatus.Rejected, glitchStatus);
            Assert.True(stitcher.IsTrackingLost);

            // Frame 3: valid frame shifted by 30px from frame 0 (15px from frame 1)
            // Re-acquisition should recover against recent frame 1
            using var frame3 = CreateSyntheticFrame(width, height, viewport, 30, 0, null);
            var recoveredStatus = stitcher.AddFrame(frame3);
            Assert.Equal(ScrollFrameStatus.Accepted, recoveredStatus);
            Assert.False(stitcher.IsTrackingLost);

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(height + 30, result.Height);
        }

        [Fact]
        public void ScenarioI_DuplicateFrames_Ignored()
        {
            const int width = 250;
            const int height = 300;
            var viewport = new Rectangle(0, 0, width, height);

            using var stitcher = new ScrollFrameStitcher();

            using var frame0 = CreateSyntheticFrame(width, height, viewport, 0, 0, null);
            Assert.Equal(ScrollFrameStatus.Accepted, stitcher.AddFrame(frame0));

            using var frame1 = CreateSyntheticFrame(width, height, viewport, 15, 0, null);
            Assert.Equal(ScrollFrameStatus.Accepted, stitcher.AddFrame(frame1));

            // Duplicate of frame 1
            using var frame1Dup = CreateSyntheticFrame(width, height, viewport, 15, 0, null);
            Assert.Equal(ScrollFrameStatus.Duplicate, stitcher.AddFrame(frame1Dup));

            Assert.Equal(2, stitcher.AcceptedFrames);
        }

        [Fact]
        public void ScenarioJ_CmdTerminal_SparseTextOnBlackBackground_NeverCollapsesViewport()
        {
            const int width = 800;
            const int height = 600;

            Image<Bgra32> CreateCmdFrame(int scrollShiftY)
            {
                var img = new Image<Bgra32>(width, height);
                img.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Bgra32(0, 0, 0, 255);
                        }
                    }

                    int[] textLineY = { 60, 100, 140, 180 };
                    foreach (int baseLine in textLineY)
                    {
                        int y = baseLine + scrollShiftY;
                        if (y >= 0 && y + 10 < height)
                        {
                            for (int ly = y; ly < y + 8; ly++)
                            {
                                var row = accessor.GetRowSpan(ly);
                                for (int x = 40; x < 400; x++)
                                {
                                    if ((x + ly) % 5 == 0)
                                    {
                                        row[x] = new Bgra32(200, 200, 200, 255);
                                    }
                                }
                            }
                        }
                    }
                });
                return img;
            }

            using var stitcher = new ScrollFrameStitcher();

            using var frame0 = CreateCmdFrame(0);
            var status0 = stitcher.AddFrame(frame0);
            Assert.Equal(ScrollFrameStatus.Accepted, status0);

            using var frame1 = CreateCmdFrame(25);
            var status1 = stitcher.AddFrame(frame1);
            Assert.Equal(ScrollFrameStatus.Accepted, status1);

            Assert.True(stitcher.Viewport.Width >= (int)(width * 0.60), $"Viewport width {stitcher.Viewport.Width} collapsed below 60%");
            Assert.True(stitcher.Viewport.Height >= (int)(height * 0.50), $"Viewport height {stitcher.Viewport.Height} collapsed below 50%");

            using var frame2 = CreateCmdFrame(50);
            var status2 = stitcher.AddFrame(frame2);
            Assert.Equal(ScrollFrameStatus.Accepted, status2);

            Assert.Equal(3, stitcher.AcceptedFrames);
            using var composite = stitcher.BuildImage();
            Assert.NotNull(composite);
            Assert.True(composite.Height >= height);
        }

        [Fact]
        public void ScenarioK_WideSidebar_ChatGPTStyle_StationaryChromeNotDuplicated_VerticalScroll()
        {
            const int width = 500;
            const int height = 400;
            const int sidebarWidth = 180; // 36% of width
            var viewport = new Rectangle(sidebarWidth, 0, width - sidebarWidth, height);

            void DrawChatGptChrome(Image<Bgra32> img)
            {
                img.ProcessPixelRows(accessor =>
                {
                    // Sidebar background: dark gray (33, 33, 33)
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < sidebarWidth; x++)
                        {
                            row[x] = new Bgra32(33, 33, 33, 255);
                        }
                    }

                    // Draw a distinctive button / avatar at y = 60..80 in the sidebar
                    for (int y = 60; y < 80; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 20; x < 60; x++)
                        {
                            row[x] = new Bgra32(255, 100, 100, 255); // Distinct red button
                        }
                    }
                });
            }

            using var stitcher = new ScrollFrameStitcher();

            // Feed 5 frames scrolling vertically by 25px each
            for (int i = 0; i < 5; i++)
            {
                using var frame = CreateSyntheticFrame(width, height, viewport, i * 25, 0, DrawChatGptChrome);
                var status = stitcher.AddFrame(frame);
                Assert.Equal(ScrollFrameStatus.Accepted, status);
            }

            Assert.Equal(5, stitcher.AcceptedFrames);
            Assert.True(stitcher.ViewportDetected);
            Assert.True(Math.Abs(stitcher.Viewport.Left - sidebarWidth) <= 2, $"Expected viewport left near {sidebarWidth}, got {stitcher.Viewport.Left}");

            using var result = stitcher.BuildImage();
            Assert.NotNull(result);
            Assert.Equal(width, result.Width);
            Assert.Equal(height + 4 * 25, result.Height);

            // Verify that the red button in the sidebar appears at y = 70, but does NOT repeat below the original height
            result.ProcessPixelRows(accessor =>
            {
                var originalRow = accessor.GetRowSpan(70);
                Assert.Equal(new Bgra32(255, 100, 100, 255), originalRow[30]);

                // At y = 70 + height = 470, the old tiling behavior would have stamped the red button again.
                // With single-render sidebar, this pixel must be the stretched background (33, 33, 33), NOT the red button!
                var belowRow = accessor.GetRowSpan(470);
                Assert.Equal(new Bgra32(33, 33, 33, 255), belowRow[30]);
            });
        }
    }
}
