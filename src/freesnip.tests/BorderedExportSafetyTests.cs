using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using freesnip.editor.forms;
using freesnip.editor.Services;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.native.foundation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpImage32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using AvaloniaPoint = Avalonia.Point;
using Xunit;

namespace freesnip.tests
{
    public class BorderedExportSafetyTests
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object? Call(ImageEditorWindow editor, string method, params object[] args)
            => typeof(ImageEditorWindow).GetMethod(method, Private)!.Invoke(editor, args);

        private static T Field<T>(ImageEditorWindow editor, string name)
            => (T)typeof(ImageEditorWindow).GetField(name, Private)!.GetValue(editor)!;

        private static Task<SharpImage> Export(ImageEditorWindow editor)
            => (Task<SharpImage>)Call(editor, "GetFlattenedImageAsync")!;

        [Fact]
        public void ApplyFrameBorder_ExpandsCanvasOutward_PreservingAllInnerAndEdgePixels()
        {
            const int originalWidth = 100;
            const int originalHeight = 100;
            const int thickness = 4;

            var green = new Rgba32(0, 255, 0, 255);
            var white = new Rgba32(255, 255, 255, 255);
            var blue = new Rgba32(0, 0, 255, 255);
            var red = new Rgba32(255, 0, 0, 255);
            var borderCol = SixLabors.ImageSharp.Color.FromRgb(0x43, 0x43, 0x43);
            var expectedBorderRgba = new Rgba32(0x43, 0x43, 0x43, 255);

            using var img = new SharpImage32(originalWidth, originalHeight, green);

            img[0, 0] = white;
            img[originalWidth - 1, 0] = white;
            img[0, originalHeight - 1] = white;
            img[originalWidth - 1, originalHeight - 1] = white;

            img[50, 0] = blue;
            img[0, 50] = blue;
            img[originalWidth - 1, 50] = blue;
            img[50, originalHeight - 1] = blue;

            img[50, 50] = red;

            EditorExportService.ApplyFrameBorder(img, thickness, borderCol);

            Assert.Equal(originalWidth + 2 * thickness, img.Width);
            Assert.Equal(originalHeight + 2 * thickness, img.Height);

            Assert.Equal(expectedBorderRgba, img[0, 0]);
            Assert.Equal(expectedBorderRgba, img[img.Width - 1, 0]);
            Assert.Equal(expectedBorderRgba, img[0, img.Height - 1]);
            Assert.Equal(expectedBorderRgba, img[img.Width - 1, img.Height - 1]);
            Assert.Equal(expectedBorderRgba, img[thickness - 1, thickness - 1]);
            Assert.Equal(expectedBorderRgba, img[img.Width - thickness, img.Height - thickness]);

            Assert.Equal(white, img[thickness, thickness]);
            Assert.Equal(white, img[thickness + originalWidth - 1, thickness]);
            Assert.Equal(white, img[thickness, thickness + originalHeight - 1]);
            Assert.Equal(white, img[thickness + originalWidth - 1, thickness + originalHeight - 1]);

            Assert.Equal(blue, img[thickness + 50, thickness]);
            Assert.Equal(blue, img[thickness, thickness + 50]);
            Assert.Equal(blue, img[thickness + originalWidth - 1, thickness + 50]);
            Assert.Equal(blue, img[thickness + 50, thickness + originalHeight - 1]);

            Assert.Equal(red, img[thickness + 50, thickness + 50]);

            Assert.Equal(green, img[thickness + 1, thickness + 1]);
            Assert.Equal(green, img[thickness + originalWidth - 2, thickness + originalHeight - 2]);
        }

        [Fact]
        public void ApplyFrameBorder_WithHexColor_ParsesCorrectly()
        {
            using var img = new SharpImage32(50, 50, new Rgba32(0, 0, 0));
            EditorExportService.ApplyFrameBorder(img, 2, "#FF0000");

            Assert.Equal(54, img.Width);
            Assert.Equal(54, img.Height);
            Assert.Equal(new Rgba32(255, 0, 0, 255), img[0, 0]);
            Assert.Equal(new Rgba32(0, 0, 0, 255), img[2, 2]);
        }

        [Fact]
        public void ApplyFrameBorder_ZeroOrNegativeThickness_DoesNotMutate()
        {
            using var img = new SharpImage32(30, 30, new Rgba32(100, 100, 100));
            EditorExportService.ApplyFrameBorder(img, 0, "#123456");
            Assert.Equal(30, img.Width);
            Assert.Equal(30, img.Height);

            EditorExportService.ApplyFrameBorder(img, -5, "#123456");
            Assert.Equal(30, img.Width);
            Assert.Equal(30, img.Height);
        }

        [AvaloniaFact]
        public async Task Export_WithBorder_DoesNotDetachVisualTree_AndPreservesOriginalEdgePixels()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.AddFrameBorders = true;
            config.FrameBorderThickness = 4;
            config.FrameBorderColor = "#434343";
            config.WarnBeforeClosingEditor = false;

            const int w = 100;
            const int h = 100;
            var green = new Rgba32(0, 255, 0, 255);
            var white = new Rgba32(255, 255, 255, 255);

            var initialImage = new SharpImage32(w, h, green);
            initialImage[0, 0] = white;
            initialImage[w - 1, 0] = white;
            initialImage[0, h - 1] = white;
            initialImage[w - 1, h - 1] = white;

            var editor = new ImageEditorWindow();
            try
            {
                await editor.SetImageAsync(initialImage, RECT.FromXYWH(0, 0, w, h));

                var canvas = Field<Canvas>(editor, "_canvas");
                var originalParent = canvas.Parent;
                Assert.NotNull(originalParent);

                var snipBorder = Field<Border>(editor, "_snipBorder");
                var overlayCanvas = Field<Canvas>(editor, "_overlayCanvas");
                var zoomContainer = Field<Panel>(editor, "_zoomContainer");

                using var exported = await Export(editor);
                using var pixels = exported.CloneAs<Rgba32>();

                Assert.Same(originalParent, canvas.Parent);

                Assert.Equal(108, exported.Width);
                Assert.Equal(108, exported.Height);

                var expectedBorder = new Rgba32(0x43, 0x43, 0x43, 255);
                Assert.Equal(expectedBorder, pixels[0, 0]);
                Assert.Equal(expectedBorder, pixels[107, 0]);
                Assert.Equal(expectedBorder, pixels[0, 107]);
                Assert.Equal(expectedBorder, pixels[107, 107]);

                Assert.Equal(white, pixels[4, 4]);
                Assert.Equal(white, pixels[103, 4]);
                Assert.Equal(white, pixels[4, 103]);
                Assert.Equal(white, pixels[103, 103]);

                Assert.NotNull(snipBorder);
                Assert.NotNull(overlayCanvas);
                Assert.NotNull(zoomContainer);
            }
            finally
            {
                editor.Close();
            }
        }

        [AvaloniaFact]
        public async Task ApplyCropRect_NonDestructive_TransformsVectors_AndRestoresOnUndo()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.AddFrameBorders = false;
            config.WarnBeforeClosingEditor = false;

            const int w = 200;
            const int h = 200;
            var initialImage = new SharpImage32(w, h, new Rgba32(255, 255, 255));

            var editor = new ImageEditorWindow();
            try
            {
                await editor.SetImageAsync(initialImage, RECT.FromXYWH(0, 0, w, h));
                var canvas = Field<Canvas>(editor, "_canvas");

                // In-bounds standard shape
                var rect = new Avalonia.Controls.Shapes.Rectangle { Width = 40, Height = 40, Fill = Brushes.Blue };
                Canvas.SetLeft(rect, 60);
                Canvas.SetTop(rect, 60);
                Call(editor, "AddAnnotation", rect);

                // In-bounds vector line
                var line = new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Green, StrokeThickness = 2 };
                Call(editor, "SetVectorAbsolutePoints", line, new AvaloniaPoint(40, 40), new AvaloniaPoint(120, 120));
                Call(editor, "AddAnnotation", line);

                // In-bounds arrow
                var arrow = new Canvas
                {
                    Tag = new ImageEditorWindow.ArrowProperties { Start = new AvaloniaPoint(50, 50), End = new AvaloniaPoint(140, 140) }
                };
                arrow.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Red, StrokeThickness = 3 });
                arrow.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = Brushes.Red });
                Call(editor, "UpdateArrowVisuals", arrow, new AvaloniaPoint(50, 50), new AvaloniaPoint(140, 140));
                Call(editor, "AddAnnotation", arrow);

                // In-bounds polyline
                var poly = new Avalonia.Controls.Shapes.Polyline
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 2,
                    Points = new List<AvaloniaPoint> { new AvaloniaPoint(0, 0), new AvaloniaPoint(20, 20) }
                };
                Canvas.SetLeft(poly, 50);
                Canvas.SetTop(poly, 50);
                Call(editor, "AddAnnotation", poly);

                // In-bounds counter
                var counter = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(15),
                    Child = new TextBlock { Text = "1", FontSize = 18 }
                };
                Canvas.SetLeft(counter, 70);
                Canvas.SetTop(counter, 70);
                Call(editor, "AddAnnotation", counter);

                // In-bounds text box
                var textBoxBorder = new Border
                {
                    Width = 50,
                    Height = 30,
                    Child = new TextBox { Text = "Test" }
                };
                Canvas.SetLeft(textBoxBorder, 80);
                Canvas.SetTop(textBoxBorder, 80);
                Call(editor, "AddAnnotation", textBoxBorder);

                // Out-of-bounds shape
                var oobRect = new Avalonia.Controls.Shapes.Rectangle { Width = 15, Height = 15, Fill = Brushes.Gray };
                Canvas.SetLeft(oobRect, 10);
                Canvas.SetTop(oobRect, 10);
                Call(editor, "AddAnnotation", oobRect);

                Assert.Equal(7, editor.GetUserAnnotations().Count);

                // Apply crop: [30, 30, 120, 120]
                await editor.ApplyCropRect(new Rect(30, 30, 120, 120));

                var doc = Field<EditorDocument>(editor, "_document");
                Assert.Equal(120, doc.Width);
                Assert.Equal(120, doc.Height);
                Assert.Equal(120.0, canvas.Width);
                Assert.Equal(120.0, canvas.Height);

                // Out-of-bounds shape removed from canvas
                Assert.DoesNotContain(oobRect, canvas.Children);
                Assert.Equal(6, editor.GetUserAnnotations().Count);

                // In-bounds shapes shifted by (-30, -30)
                Assert.Contains(rect, canvas.Children);
                Assert.Equal(30.0, Canvas.GetLeft(rect));
                Assert.Equal(30.0, Canvas.GetTop(rect));

                // Line shifted
                Assert.Contains(line, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var lStart, out var lEnd));
                Assert.Equal(new AvaloniaPoint(10, 10), lStart);
                Assert.Equal(new AvaloniaPoint(90, 90), lEnd);

                // Arrow shifted
                Assert.Contains(arrow, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(arrow, out var aStart, out var aEnd));
                Assert.Equal(new AvaloniaPoint(20, 20), aStart);
                Assert.Equal(new AvaloniaPoint(110, 110), aEnd);

                // Polyline shifted without re-projecting individual points
                Assert.Contains(poly, canvas.Children);
                Assert.Equal(20.0, Canvas.GetLeft(poly));
                Assert.Equal(20.0, Canvas.GetTop(poly));
                Assert.Equal(new AvaloniaPoint(0, 0), poly.Points[0]);
                Assert.Equal(new AvaloniaPoint(20, 20), poly.Points[1]);

                // Counter shifted
                Assert.Contains(counter, canvas.Children);
                Assert.Equal(40.0, Canvas.GetLeft(counter));
                Assert.Equal(40.0, Canvas.GetTop(counter));

                // Text box shifted
                Assert.Contains(textBoxBorder, canvas.Children);
                Assert.Equal(50.0, Canvas.GetLeft(textBoxBorder));
                Assert.Equal(50.0, Canvas.GetTop(textBoxBorder));

                // Undo
                bool undone = await editor.PerformUndoAsync();
                Assert.True(undone);

                Assert.Equal(200, doc.Width);
                Assert.Equal(200, doc.Height);
                Assert.Equal(200.0, canvas.Width);
                Assert.Equal(200.0, canvas.Height);

                // Out-of-bounds shape restored
                Assert.Equal(7, editor.GetUserAnnotations().Count);
                var restoredOob = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Gray);
                Assert.Equal(10.0, Canvas.GetLeft(restoredOob));
                Assert.Equal(10.0, Canvas.GetTop(restoredOob));

                var restoredRect = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Blue);
                Assert.Equal(60.0, Canvas.GetLeft(restoredRect));
                Assert.Equal(60.0, Canvas.GetTop(restoredRect));

                var restoredLine = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Line>());
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredLine, out var rlStart, out var rlEnd));
                Assert.Equal(new AvaloniaPoint(40, 40), rlStart);
                Assert.Equal(new AvaloniaPoint(120, 120), rlEnd);

                var restoredArrow = Assert.Single(canvas.Children.OfType<Canvas>(), c => c.Tag is ImageEditorWindow.ArrowProperties);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredArrow, out var raStart, out var raEnd));
                Assert.Equal(new AvaloniaPoint(50, 50), raStart);
                Assert.Equal(new AvaloniaPoint(140, 140), raEnd);
            }
            finally
            {
                editor.Close();
            }
        }

        [AvaloniaFact]
        public async Task ApplyCutOutSlice_Vertical_NonDestructive_TransformsVectors_AndRestoresOnUndo()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.AddFrameBorders = false;
            config.WarnBeforeClosingEditor = false;

            const int w = 300;
            const int h = 200;
            var initialImage = new SharpImage32(w, h, new Rgba32(255, 255, 255));

            var editor = new ImageEditorWindow();
            try
            {
                await editor.SetImageAsync(initialImage, RECT.FromXYWH(0, 0, w, h));
                var canvas = Field<Canvas>(editor, "_canvas");

                // 1. Strictly to the left (Right = 50 <= 100)
                var leftRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Blue };
                Canvas.SetLeft(leftRect, 20);
                Canvas.SetTop(leftRect, 50);
                Call(editor, "AddAnnotation", leftRect);

                // 2. Strictly to the right (Left = 200 >= 150)
                var rightRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Red };
                Canvas.SetLeft(rightRect, 200);
                Canvas.SetTop(rightRect, 50);
                Call(editor, "AddAnnotation", rightRect);

                // 3. Completely inside the cut [100, 150] (Left = 110, Right = 130)
                var insideRect = new Avalonia.Controls.Shapes.Rectangle { Width = 20, Height = 30, Fill = Brushes.Green };
                Canvas.SetLeft(insideRect, 110);
                Canvas.SetTop(insideRect, 50);
                Call(editor, "AddAnnotation", insideRect);

                // 4. Spanning across the cut (Left = 80, Width = 100 -> Right = 180)
                var spanningRect = new Avalonia.Controls.Shapes.Rectangle { Width = 100, Height = 30, Fill = Brushes.Purple };
                Canvas.SetLeft(spanningRect, 80);
                Canvas.SetTop(spanningRect, 50);
                Call(editor, "AddAnnotation", spanningRect);

                // 5. Spanning vector line (Start = 50, End = 200)
                var line = new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Black, StrokeThickness = 3 };
                Call(editor, "SetVectorAbsolutePoints", line, new AvaloniaPoint(50, 60), new AvaloniaPoint(200, 60));
                Call(editor, "AddAnnotation", line);

                // 6. Spanning arrow (Start = 60, End = 220)
                var arrow = new Canvas
                {
                    Tag = new ImageEditorWindow.ArrowProperties { Start = new AvaloniaPoint(60, 70), End = new AvaloniaPoint(220, 70) }
                };
                arrow.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Orange, StrokeThickness = 3 });
                arrow.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = Brushes.Orange });
                Call(editor, "UpdateArrowVisuals", arrow, new AvaloniaPoint(60, 70), new AvaloniaPoint(220, 70));
                Call(editor, "AddAnnotation", arrow);

                Assert.Equal(6, editor.GetUserAnnotations().Count);

                // Vertical cut: x = 100, width = 50
                await editor.ApplyCutOutSlice(new Rect(100, 0, 50, 200), true);

                var doc = Field<EditorDocument>(editor, "_document");
                Assert.Equal(250, doc.Width);
                Assert.Equal(200, doc.Height);
                Assert.Equal(250.0, canvas.Width);
                Assert.Equal(200.0, canvas.Height);

                // Inside rect deleted from canvas
                Assert.DoesNotContain(insideRect, canvas.Children);
                Assert.Equal(5, editor.GetUserAnnotations().Count);

                // Left rect unchanged
                Assert.Contains(leftRect, canvas.Children);
                Assert.Equal(20.0, Canvas.GetLeft(leftRect));
                Assert.Equal(30.0, leftRect.Width);

                // Right rect shifted by -50
                Assert.Contains(rightRect, canvas.Children);
                Assert.Equal(150.0, Canvas.GetLeft(rightRect));
                Assert.Equal(30.0, rightRect.Width);

                // Spanning rect clamped: width = 100 - 50 = 50
                Assert.Contains(spanningRect, canvas.Children);
                Assert.Equal(80.0, Canvas.GetLeft(spanningRect));
                Assert.Equal(50.0, spanningRect.Width);

                // Spanning line endpoints adjusted across seam
                Assert.Contains(line, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var lStart, out var lEnd));
                Assert.Equal(new AvaloniaPoint(50, 60), lStart);
                Assert.Equal(new AvaloniaPoint(150, 60), lEnd);

                // Spanning arrow endpoints adjusted across seam
                Assert.Contains(arrow, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(arrow, out var aStart, out var aEnd));
                Assert.Equal(new AvaloniaPoint(60, 70), aStart);
                Assert.Equal(new AvaloniaPoint(170, 70), aEnd);

                // Undo
                bool undone = await editor.PerformUndoAsync();
                Assert.True(undone);

                Assert.Equal(300, doc.Width);
                Assert.Equal(200, doc.Height);
                Assert.Equal(300.0, canvas.Width);
                Assert.Equal(200.0, canvas.Height);

                // Inside rect restored
                Assert.Equal(6, editor.GetUserAnnotations().Count);
                var restoredInside = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Green);
                Assert.Equal(110.0, Canvas.GetLeft(restoredInside));
                Assert.Equal(20.0, restoredInside.Width);

                var restoredRight = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Red);
                Assert.Equal(200.0, Canvas.GetLeft(restoredRight));

                var restoredSpanning = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Purple);
                Assert.Equal(80.0, Canvas.GetLeft(restoredSpanning));
                Assert.Equal(100.0, restoredSpanning.Width);

                var restoredLine = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Line>());
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredLine, out var rlStart, out var rlEnd));
                Assert.Equal(new AvaloniaPoint(50, 60), rlStart);
                Assert.Equal(new AvaloniaPoint(200, 60), rlEnd);

                var restoredArrow = Assert.Single(canvas.Children.OfType<Canvas>(), c => c.Tag is ImageEditorWindow.ArrowProperties);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredArrow, out var raStart, out var raEnd));
                Assert.Equal(new AvaloniaPoint(60, 70), raStart);
                Assert.Equal(new AvaloniaPoint(220, 70), raEnd);
            }
            finally
            {
                editor.Close();
            }
        }

        [AvaloniaFact]
        public async Task ApplyCutOutSlice_Horizontal_NonDestructive_TransformsVectors_AndRestoresOnUndo()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.AddFrameBorders = false;
            config.WarnBeforeClosingEditor = false;

            const int w = 200;
            const int h = 300;
            var initialImage = new SharpImage32(w, h, new Rgba32(255, 255, 255));

            var editor = new ImageEditorWindow();
            try
            {
                await editor.SetImageAsync(initialImage, RECT.FromXYWH(0, 0, w, h));
                var canvas = Field<Canvas>(editor, "_canvas");

                // 1. Strictly above (Bottom = 50 <= 100)
                var topRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Blue };
                Canvas.SetLeft(topRect, 50);
                Canvas.SetTop(topRect, 20);
                Call(editor, "AddAnnotation", topRect);

                // 2. Strictly below (Top = 200 >= 150)
                var bottomRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Red };
                Canvas.SetLeft(bottomRect, 50);
                Canvas.SetTop(bottomRect, 200);
                Call(editor, "AddAnnotation", bottomRect);

                // 3. Completely inside the cut [100, 150] (Top = 110, Bottom = 130)
                var insideRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 20, Fill = Brushes.Green };
                Canvas.SetLeft(insideRect, 50);
                Canvas.SetTop(insideRect, 110);
                Call(editor, "AddAnnotation", insideRect);

                // 4. Spanning across the cut (Top = 80, Height = 100 -> Bottom = 180)
                var spanningRect = new Avalonia.Controls.Shapes.Rectangle { Width = 30, Height = 100, Fill = Brushes.Purple };
                Canvas.SetLeft(spanningRect, 50);
                Canvas.SetTop(spanningRect, 80);
                Call(editor, "AddAnnotation", spanningRect);

                // 5. Spanning vector line (Start = 50, End = 200)
                var line = new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Black, StrokeThickness = 3 };
                Call(editor, "SetVectorAbsolutePoints", line, new AvaloniaPoint(60, 50), new AvaloniaPoint(60, 200));
                Call(editor, "AddAnnotation", line);

                // 6. Spanning arrow (Start = 60, End = 220)
                var arrow = new Canvas
                {
                    Tag = new ImageEditorWindow.ArrowProperties { Start = new AvaloniaPoint(70, 60), End = new AvaloniaPoint(70, 220) }
                };
                arrow.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Orange, StrokeThickness = 3 });
                arrow.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = Brushes.Orange });
                Call(editor, "UpdateArrowVisuals", arrow, new AvaloniaPoint(70, 60), new AvaloniaPoint(70, 220));
                Call(editor, "AddAnnotation", arrow);

                Assert.Equal(6, editor.GetUserAnnotations().Count);

                // Horizontal cut: y = 100, height = 50
                await editor.ApplyCutOutSlice(new Rect(0, 100, 200, 50), false);

                var doc = Field<EditorDocument>(editor, "_document");
                Assert.Equal(200, doc.Width);
                Assert.Equal(250, doc.Height);
                Assert.Equal(200.0, canvas.Width);
                Assert.Equal(250.0, canvas.Height);

                // Inside rect deleted from canvas
                Assert.DoesNotContain(insideRect, canvas.Children);
                Assert.Equal(5, editor.GetUserAnnotations().Count);

                // Top rect unchanged
                Assert.Contains(topRect, canvas.Children);
                Assert.Equal(20.0, Canvas.GetTop(topRect));
                Assert.Equal(30.0, topRect.Height);

                // Bottom rect shifted by -50
                Assert.Contains(bottomRect, canvas.Children);
                Assert.Equal(150.0, Canvas.GetTop(bottomRect));
                Assert.Equal(30.0, bottomRect.Height);

                // Spanning rect clamped: height = 100 - 50 = 50
                Assert.Contains(spanningRect, canvas.Children);
                Assert.Equal(80.0, Canvas.GetTop(spanningRect));
                Assert.Equal(50.0, spanningRect.Height);

                // Spanning line endpoints adjusted across seam
                Assert.Contains(line, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var lStart, out var lEnd));
                Assert.Equal(new AvaloniaPoint(60, 50), lStart);
                Assert.Equal(new AvaloniaPoint(60, 150), lEnd);

                // Spanning arrow endpoints adjusted across seam
                Assert.Contains(arrow, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(arrow, out var aStart, out var aEnd));
                Assert.Equal(new AvaloniaPoint(70, 60), aStart);
                Assert.Equal(new AvaloniaPoint(70, 170), aEnd);

                // Undo
                bool undone = await editor.PerformUndoAsync();
                Assert.True(undone);

                Assert.Equal(200, doc.Width);
                Assert.Equal(300, doc.Height);
                Assert.Equal(200.0, canvas.Width);
                Assert.Equal(300.0, canvas.Height);

                // Inside rect restored
                Assert.Equal(6, editor.GetUserAnnotations().Count);
                var restoredInside = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Green);
                Assert.Equal(110.0, Canvas.GetTop(restoredInside));
                Assert.Equal(20.0, restoredInside.Height);

                var restoredBottom = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Red);
                Assert.Equal(200.0, Canvas.GetTop(restoredBottom));

                var restoredSpanning = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Purple);
                Assert.Equal(80.0, Canvas.GetTop(restoredSpanning));
                Assert.Equal(100.0, restoredSpanning.Height);

                var restoredLine = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Line>());
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredLine, out var rlStart, out var rlEnd));
                Assert.Equal(new AvaloniaPoint(60, 50), rlStart);
                Assert.Equal(new AvaloniaPoint(60, 200), rlEnd);

                var restoredArrow = Assert.Single(canvas.Children.OfType<Canvas>(), c => c.Tag is ImageEditorWindow.ArrowProperties);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredArrow, out var raStart, out var raEnd));
                Assert.Equal(new AvaloniaPoint(70, 60), raStart);
                Assert.Equal(new AvaloniaPoint(70, 220), raEnd);
            }
            finally
            {
                editor.Close();
            }
        }

        [AvaloniaFact]
        public async Task ApplyResize_NonDestructive_ScalesVectors_AndRestoresOnUndo()
        {
            var config = IniConfig.GetIniSection<CoreConfiguration>(allowSave: false);
            config.AddFrameBorders = false;
            config.WarnBeforeClosingEditor = false;

            const int w = 200;
            const int h = 200;
            var initialImage = new SharpImage32(w, h, new Rgba32(255, 255, 255));

            var editor = new ImageEditorWindow();
            try
            {
                await editor.SetImageAsync(initialImage, RECT.FromXYWH(0, 0, w, h));
                var canvas = Field<Canvas>(editor, "_canvas");

                // 1. Standard rectangle: [60, 60, 40, 40]
                var rect = new Avalonia.Controls.Shapes.Rectangle { Width = 40, Height = 40, Fill = Brushes.Blue };
                Canvas.SetLeft(rect, 60);
                Canvas.SetTop(rect, 60);
                Call(editor, "AddAnnotation", rect);

                // 2. Vector line: (40, 40) -> (120, 120)
                var line = new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Green, StrokeThickness = 2 };
                Call(editor, "SetVectorAbsolutePoints", line, new AvaloniaPoint(40, 40), new AvaloniaPoint(120, 120));
                Call(editor, "AddAnnotation", line);

                // 3. Arrow: (50, 50) -> (140, 140)
                var arrow = new Canvas
                {
                    Tag = new ImageEditorWindow.ArrowProperties { Start = new AvaloniaPoint(50, 50), End = new AvaloniaPoint(140, 140) }
                };
                arrow.Children.Add(new Avalonia.Controls.Shapes.Line { Stroke = Brushes.Red, StrokeThickness = 3 });
                arrow.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = Brushes.Red });
                Call(editor, "UpdateArrowVisuals", arrow, new AvaloniaPoint(50, 50), new AvaloniaPoint(140, 140));
                Call(editor, "AddAnnotation", arrow);

                // 4. Polyline: Left = 50, Top = 50, Points = [(0, 0), (20, 20)]
                var poly = new Avalonia.Controls.Shapes.Polyline
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 2,
                    Points = new List<AvaloniaPoint> { new AvaloniaPoint(0, 0), new AvaloniaPoint(20, 20) }
                };
                Canvas.SetLeft(poly, 50);
                Canvas.SetTop(poly, 50);
                Call(editor, "AddAnnotation", poly);

                // 5. Counter: Left = 70, Top = 70, Width = 30, Height = 30
                var counter = new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(15),
                    Child = new TextBlock { Text = "1", FontSize = 18 }
                };
                Canvas.SetLeft(counter, 70);
                Canvas.SetTop(counter, 70);
                Call(editor, "AddAnnotation", counter);

                // 6. Text box: Left = 80, Top = 80, Width = 50, Height = 30
                var textBoxBorder = new Border
                {
                    Width = 50,
                    Height = 30,
                    Child = new TextBox { Text = "Test", FontSize = 16 }
                };
                Canvas.SetLeft(textBoxBorder, 80);
                Canvas.SetTop(textBoxBorder, 80);
                Call(editor, "AddAnnotation", textBoxBorder);

                // 7. Emoji: Left = 90, Top = 90, FontSize = 40
                var emoji = new TextBlock
                {
                    Text = "😊",
                    FontSize = 40,
                    Width = 40,
                    Height = 40
                };
                Canvas.SetLeft(emoji, 90);
                Canvas.SetTop(emoji, 90);
                Call(editor, "AddAnnotation", emoji);

                Assert.Equal(7, editor.GetUserAnnotations().Count);

                // Resize from 200x200 to 400x300 (scaleX = 2.0, scaleY = 1.5)
                await editor.ApplyResize(400, 300);

                var doc = Field<EditorDocument>(editor, "_document");
                Assert.Equal(400, doc.Width);
                Assert.Equal(300, doc.Height);
                Assert.Equal(400.0, canvas.Width);
                Assert.Equal(300.0, canvas.Height);

                // All 7 controls must be preserved as editable vector controls (NOT flattened)
                Assert.Equal(7, editor.GetUserAnnotations().Count);

                // 1. Rectangle scaled: Left = 60 * 2.0 = 120, Top = 60 * 1.5 = 90, Width = 40 * 2.0 = 80, Height = 40 * 1.5 = 60
                Assert.Contains(rect, canvas.Children);
                Assert.Equal(120.0, Canvas.GetLeft(rect));
                Assert.Equal(90.0, Canvas.GetTop(rect));
                Assert.Equal(80.0, rect.Width);
                Assert.Equal(60.0, rect.Height);

                // 2. Line scaled: (40 * 2, 40 * 1.5) -> (120 * 2, 120 * 1.5) = (80, 60) -> (240, 180)
                Assert.Contains(line, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(line, out var lStart, out var lEnd));
                Assert.Equal(new AvaloniaPoint(80, 60), lStart);
                Assert.Equal(new AvaloniaPoint(240, 180), lEnd);

                // 3. Arrow scaled: (50 * 2, 50 * 1.5) -> (140 * 2, 140 * 1.5) = (100, 75) -> (280, 210)
                Assert.Contains(arrow, canvas.Children);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(arrow, out var aStart, out var aEnd));
                Assert.Equal(new AvaloniaPoint(100, 75), aStart);
                Assert.Equal(new AvaloniaPoint(280, 210), aEnd);

                // 4. Polyline scaled: Left = 50 * 2 = 100, Top = 50 * 1.5 = 75, Points = [(0, 0), (40, 30)]
                Assert.Contains(poly, canvas.Children);
                Assert.Equal(100.0, Canvas.GetLeft(poly));
                Assert.Equal(75.0, Canvas.GetTop(poly));
                Assert.Equal(new AvaloniaPoint(0, 0), poly.Points[0]);
                Assert.Equal(new AvaloniaPoint(40, 30), poly.Points[1]);

                // 5. Counter scaled: Left = 70 * 2 = 140, Top = 70 * 1.5 = 105, Width = 30 * 2 = 60, Height = 30 * 1.5 = 45
                Assert.Contains(counter, canvas.Children);
                Assert.Equal(140.0, Canvas.GetLeft(counter));
                Assert.Equal(105.0, Canvas.GetTop(counter));
                Assert.Equal(60.0, counter.Width);
                Assert.Equal(45.0, counter.Height);

                // 6. Text box scaled: Left = 80 * 2 = 160, Top = 80 * 1.5 = 120, Width = 50 * 2 = 100, Height = 30 * 1.5 = 45
                Assert.Contains(textBoxBorder, canvas.Children);
                Assert.Equal(160.0, Canvas.GetLeft(textBoxBorder));
                Assert.Equal(120.0, Canvas.GetTop(textBoxBorder));
                Assert.Equal(100.0, textBoxBorder.Width);
                Assert.Equal(45.0, textBoxBorder.Height);

                // 7. Emoji scaled: Left = 90 * 2 = 180, Top = 90 * 1.5 = 135
                Assert.Contains(emoji, canvas.Children);
                Assert.Equal(180.0, Canvas.GetLeft(emoji));
                Assert.Equal(135.0, Canvas.GetTop(emoji));
                Assert.Equal(70.0, emoji.FontSize);

                // Undo
                bool undone = await editor.PerformUndoAsync();
                Assert.True(undone);

                Assert.Equal(200, doc.Width);
                Assert.Equal(200, doc.Height);
                Assert.Equal(200.0, canvas.Width);
                Assert.Equal(200.0, canvas.Height);

                // All 7 controls restored to exact pre-resize positions and sizes
                Assert.Equal(7, editor.GetUserAnnotations().Count);

                var restoredRect = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>(), r => r.Fill == Brushes.Blue);
                Assert.Equal(60.0, Canvas.GetLeft(restoredRect));
                Assert.Equal(60.0, Canvas.GetTop(restoredRect));
                Assert.Equal(40.0, restoredRect.Width);
                Assert.Equal(40.0, restoredRect.Height);

                var restoredLine = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Line>());
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredLine, out var rlStart, out var rlEnd));
                Assert.Equal(new AvaloniaPoint(40, 40), rlStart);
                Assert.Equal(new AvaloniaPoint(120, 120), rlEnd);

                var restoredArrow = Assert.Single(canvas.Children.OfType<Canvas>(), c => c.Tag is ImageEditorWindow.ArrowProperties);
                Assert.True(ImageEditorWindow.TryGetVectorAbsolutePoints(restoredArrow, out var raStart, out var raEnd));
                Assert.Equal(new AvaloniaPoint(50, 50), raStart);
                Assert.Equal(new AvaloniaPoint(140, 140), raEnd);

                var restoredPoly = Assert.Single(canvas.Children.OfType<Avalonia.Controls.Shapes.Polyline>());
                Assert.Equal(50.0, Canvas.GetLeft(restoredPoly));
                Assert.Equal(50.0, Canvas.GetTop(restoredPoly));
                Assert.Equal(new AvaloniaPoint(0, 0), restoredPoly.Points[0]);
                Assert.Equal(new AvaloniaPoint(20, 20), restoredPoly.Points[1]);

                var restoredCounter = Assert.Single(editor.GetUserAnnotations().OfType<Border>(), b => b.Child is TextBlock);
                Assert.Equal(70.0, Canvas.GetLeft(restoredCounter));
                Assert.Equal(70.0, Canvas.GetTop(restoredCounter));
                Assert.Equal(30.0, restoredCounter.Width);
                Assert.Equal(30.0, restoredCounter.Height);

                var restoredTextBox = Assert.Single(editor.GetUserAnnotations().OfType<Border>(), b => b.Child is TextBox);
                Assert.Equal(80.0, Canvas.GetLeft(restoredTextBox));
                Assert.Equal(80.0, Canvas.GetTop(restoredTextBox));
                Assert.Equal(50.0, restoredTextBox.Width);
                Assert.Equal(30.0, restoredTextBox.Height);

                var restoredEmoji = Assert.Single(canvas.Children.OfType<TextBlock>(), tb => tb.Text == "😊");
                Assert.Equal(90.0, Canvas.GetLeft(restoredEmoji));
                Assert.Equal(90.0, Canvas.GetTop(restoredEmoji));
                Assert.Equal(40.0, restoredEmoji.FontSize);
            }
            finally
            {
                editor.Close();
            }
        }
    }
}

