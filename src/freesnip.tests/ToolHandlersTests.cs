#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using freesnip.editor.forms;
using freesnip.editor.Services;
using freesnip.editor.Services.Tools;
using freesnip.foundation.core;
using Xunit;

namespace freesnip.tests
{
    public class FakeToolBridge : IToolContextBridge
    {
        public Canvas? Canvas => null;
        public Size CurrentImageSize { get; set; } = new Size(800, 600);
        public double ZoomFactor => 1.0;
        public EditorHistoryManager History => null!;
        public IBrush CurrentBrush { get; set; } = Brushes.Red;
        public double CurrentThickness { get; set; } = 3.0;
        public string PendingEmoji { get; set; } = "🚀";
        public int CounterValue { get; set; } = 1;
        public bool IsFillMode { get; set; } = false;
        public bool GetToolFillMode(EditorTool tool) => false;
        public bool IsDrawing { get; set; }
        public Point StartPoint { get; set; }

        public List<string> CallLog { get; } = new();
        public Point LastPreviewStart { get; private set; }
        public Point LastPreviewEnd { get; private set; }
        public Point LastCommitStart { get; private set; }
        public Point LastCommitEnd { get; private set; }
        public EditorTool LastCommittedTool { get; private set; }
        public bool PointerCaptured { get; private set; }

        public void CaptureUndoCheckpoint(bool cloneImage = true) => CallLog.Add("CaptureUndoCheckpoint");
        public void SetEditorCursor(Cursor cursor) => CallLog.Add("SetEditorCursor");
        public void ShowToolGhost(Point pos) => CallLog.Add($"ShowToolGhost({pos.X},{pos.Y})");
        public void HideToolGhost() => CallLog.Add("HideToolGhost");
        public void ShowSnapGuides() => CallLog.Add("ShowSnapGuides");
        public void HideSnapGuides() => CallLog.Add("HideSnapGuides");
        public void ShowSnapHint(Point pos, bool altPressed) => CallLog.Add("ShowSnapHint");
        public void HideSnapHint() => CallLog.Add("HideSnapHint");
        public void ShowToast(string message) => CallLog.Add($"ShowToast:{message}");
        public void CapturePointer(bool capture) { PointerCaptured = capture; CallLog.Add($"CapturePointer:{capture}"); }
        public void UpdateMagnetButtonState(bool altPressed) => CallLog.Add($"UpdateMagnet:{altPressed}");

        public Point SnapToNearbyTarget(Point target, Point fallback) => fallback;
        public Point ClampRectDrawEnd(Point end) => end;
        public Point ApplyVectorConstraints(Point current, Point anchor, KeyModifiers modifiers, bool allowTargetSnap) => current;
        public void TriggerSnapGlowEffect(Point point) => CallLog.Add("TriggerSnapGlowEffect");
        public void RefreshSnapTargetsList(Control? exclude, Point currentPos) { }
        public void HighlightSnapDot(Point? snappedPoint, Point currentPos) { }
        public void UpdateVectorInfo(Point start, Point end, bool altPressed) => CallLog.Add("UpdateVectorInfo");
        public void HideVectorInfo() => CallLog.Add("HideVectorInfo");

        public void BeginPreviewShape(Point start, IBrush brush, EditorTool tool)
        {
            LastPreviewStart = start;
            CallLog.Add($"BeginPreviewShape:{tool}");
        }
        public void UpdatePreviewShape(Point end)
        {
            LastPreviewEnd = end;
            CallLog.Add($"UpdatePreviewShape:{end.X},{end.Y}");
        }
        public void RemovePreviewShape() => CallLog.Add("RemovePreviewShape");
        public void CommitShape(Point start, Point end, EditorTool tool)
        {
            LastCommitStart = start;
            LastCommitEnd = end;
            LastCommittedTool = tool;
            CallLog.Add($"CommitShape:{tool}");
        }
        public void AddAnnotation(Control control) => CallLog.Add("AddAnnotation");
        public void FinalizeSelectedPasteObject() => CallLog.Add("FinalizeSelectedPasteObject");
        public void ClearSelection() => CallLog.Add("ClearSelection");

        public void PlaceCounter(Point pos, IBrush brush) => CallLog.Add($"PlaceCounter:{pos.X},{pos.Y}");
        public void PlaceEmoji(Point pos, string emoji) => CallLog.Add($"PlaceEmoji:{emoji}");
        public void BeginFreeDraw(Point start, IBrush brush) => CallLog.Add($"BeginFreeDraw:{start.X},{start.Y}");
        public void AddFreeDrawPoint(Point pos) => CallLog.Add($"AddFreeDrawPoint:{pos.X},{pos.Y}");
        public void EndFreeDraw() => CallLog.Add("EndFreeDraw");
        public Point ApplyCropModeToStart(Point pt) => pt;
        public Point ApplyCropModeToEnd(Point pt) => pt;
        public void CommitCrop(Point start, Point end)
        {
            LastCommitStart = start;
            LastCommitEnd = end;
            LastCommittedTool = EditorTool.Crop;
            CallLog.Add("CommitCrop");
        }
    }

    public class ToolHandlersTests
    {
        private static ToolPointerEvent MakeEvent(Point pos, bool isLeft = true, KeyModifiers mod = KeyModifiers.None)
        {
            return new ToolPointerEvent(
                canvasPosition: pos,
                windowPosition: pos,
                modifiers: mod,
                isLeftButtonPressed: isLeft,
                isRightButtonPressed: false,
                isMiddleButtonPressed: false,
                clickCount: 1,
                isCaptured: false);
        }

        [Fact]
        public void RectangleToolHandler_Lifecycle_DrawsAndCommitsRectangle()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var handler = new RectangleToolHandler();

            Assert.Equal(EditorTool.Rectangle, handler.Tool);

            handler.OnPointerPressed(context, MakeEvent(new Point(10, 20)));
            Assert.True(context.IsDrawing);
            Assert.Equal(new Point(10, 20), bridge.LastPreviewStart);
            Assert.True(bridge.PointerCaptured);

            handler.OnPointerMoved(context, MakeEvent(new Point(100, 150)));
            Assert.Equal(new Point(100, 150), bridge.LastPreviewEnd);

            handler.OnPointerReleased(context, MakeEvent(new Point(100, 150)));
            Assert.False(context.IsDrawing);
            Assert.Equal(new Point(10, 20), bridge.LastCommitStart);
            Assert.Equal(new Point(100, 150), bridge.LastCommitEnd);
            Assert.Equal(EditorTool.Rectangle, bridge.LastCommittedTool);
            Assert.False(bridge.PointerCaptured);
        }

        [Fact]
        public void EllipseToolHandler_Lifecycle_CommitsEllipse()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var handler = new EllipseToolHandler();

            Assert.Equal(EditorTool.Ellipse, handler.Tool);

            handler.OnPointerPressed(context, MakeEvent(new Point(5, 5)));
            handler.OnPointerMoved(context, MakeEvent(new Point(50, 50)));
            handler.OnPointerReleased(context, MakeEvent(new Point(50, 50)));

            Assert.Equal(EditorTool.Ellipse, bridge.LastCommittedTool);
            Assert.Equal(new Point(5, 5), bridge.LastCommitStart);
            Assert.Equal(new Point(50, 50), bridge.LastCommitEnd);
        }

        [Fact]
        public void ArrowAndLineToolHandlers_UpdateVectorInfo()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var arrowHandler = new ArrowToolHandler();
            var lineHandler = new LineToolHandler();

            Assert.Equal(EditorTool.Arrow, arrowHandler.Tool);
            Assert.Equal(EditorTool.Line, lineHandler.Tool);

            arrowHandler.OnPointerPressed(context, MakeEvent(new Point(0, 0)));
            arrowHandler.OnPointerMoved(context, MakeEvent(new Point(100, 100)));
            Assert.Contains("UpdateVectorInfo", bridge.CallLog);
            arrowHandler.OnPointerReleased(context, MakeEvent(new Point(100, 100)));
            Assert.Equal(EditorTool.Arrow, bridge.LastCommittedTool);

            bridge.CallLog.Clear();
            lineHandler.OnPointerPressed(context, MakeEvent(new Point(10, 10)));
            lineHandler.OnPointerMoved(context, MakeEvent(new Point(60, 60)));
            Assert.Contains("UpdateVectorInfo", bridge.CallLog);
            lineHandler.OnPointerReleased(context, MakeEvent(new Point(60, 60)));
            Assert.Equal(EditorTool.Line, bridge.LastCommittedTool);
        }

        [Fact]
        public void FreeDrawToolHandler_AppendsPoints()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var handler = new FreeDrawToolHandler();

            handler.OnPointerPressed(context, MakeEvent(new Point(5, 5)));
            Assert.True(context.IsDrawing);
            Assert.Contains("BeginFreeDraw:5,5", bridge.CallLog);

            handler.OnPointerMoved(context, MakeEvent(new Point(15, 25)));
            Assert.Contains("AddFreeDrawPoint:15,25", bridge.CallLog);

            handler.OnPointerReleased(context, MakeEvent(new Point(15, 25)));
            Assert.False(context.IsDrawing);
            Assert.Contains("EndFreeDraw", bridge.CallLog);
        }

        [Fact]
        public void CounterAndEmojiToolHandlers_DoNotKeepDrawing()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var counterHandler = new CounterToolHandler();
            var emojiHandler = new EmojiToolHandler();

            counterHandler.OnPointerPressed(context, MakeEvent(new Point(40, 50)));
            Assert.False(context.IsDrawing);
            Assert.Contains("PlaceCounter:40,50", bridge.CallLog);

            emojiHandler.OnPointerPressed(context, MakeEvent(new Point(60, 70)));
            Assert.False(context.IsDrawing);
            Assert.Contains("PlaceEmoji:🚀", bridge.CallLog);
        }

        [Fact]
        public void CropToolHandler_CommitsCrop()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var cropHandler = new CropToolHandler();

            cropHandler.OnPointerPressed(context, MakeEvent(new Point(20, 30)));
            Assert.True(context.IsDrawing);

            cropHandler.OnPointerMoved(context, MakeEvent(new Point(200, 300)));
            cropHandler.OnPointerReleased(context, MakeEvent(new Point(200, 300)));

            Assert.False(context.IsDrawing);
            Assert.Equal(EditorTool.Crop, bridge.LastCommittedTool);
            Assert.Equal(new Point(20, 30), bridge.LastCommitStart);
            Assert.Equal(new Point(200, 300), bridge.LastCommitEnd);
        }

        [Fact]
        public void NoneToolHandler_HidesChromeOnActivate()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var handler = new NoneToolHandler();

            handler.OnActivated(context);
            Assert.Contains("HideToolGhost", bridge.CallLog);
            Assert.Contains("HideSnapGuides", bridge.CallLog);
            Assert.Contains("HideVectorInfo", bridge.CallLog);
        }

        [Fact]
        public void TextToolHandler_InitializesAndCommitsProperly()
        {
            var bridge = new FakeToolBridge();
            var context = new ToolHandlerContext(bridge);
            var textHandler = new TextToolHandler();

            Assert.Equal(EditorTool.Text, textHandler.Tool);

            textHandler.OnPointerPressed(context, MakeEvent(new Point(10, 20)));
            Assert.True(context.IsDrawing);
            Assert.Contains("BeginPreviewShape:Text", bridge.CallLog);

            textHandler.OnPointerReleased(context, MakeEvent(new Point(100, 150)));
            Assert.False(context.IsDrawing);
            Assert.Contains("CommitShape:Text", bridge.CallLog);
            Assert.Equal(EditorTool.Text, bridge.LastCommittedTool);
            Assert.Equal(new Point(10, 20), bridge.LastCommitStart);
            Assert.Equal(new Point(100, 150), bridge.LastCommitEnd);
        }

        [AvaloniaFact]
        public void TextTool_TypingMultilineText_PreservesFontSize_AndExpandsHeightDownwards()
        {
            var config = new CoreConfiguration();
            var editor = new ImageEditorWindow();
            try
            {
                var textBox = new TextBox
                {
                    FontSize = 24,
                    Text = "Initial Text",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeight.SemiBold,
                    FontFamily = FontFamily.Default
                };
                var border = new Border
                {
                    Width = 160,
                    Height = 48,
                    BorderThickness = new Thickness(2),
                    Padding = new Thickness(2),
                    Child = textBox
                };

                ImageEditorWindow.SetTextBoxBoundsToBorder(border, textBox);
                editor.AttachTextBoxBehavior(border, textBox, config);

                Assert.Equal(24.0, textBox.FontSize);
                double initialHeight = border.Height;
                Assert.Equal(48.0, initialHeight);
                double initialWidth = border.Width;

                // Simulate typing long multiline text that exceeds available height
                textBox.Text = "Line One of the annotation\nLine Two that definitely wraps and requires more vertical height\nLine Three of text";

                // Invariant: FontSize MUST be locked and preserved
                Assert.Equal(24.0, textBox.FontSize);

                // Invariant: Border Height must dynamically expand downwards
                Assert.True(border.Height > initialHeight, $"Border height {border.Height} should have expanded beyond {initialHeight}");

                // Invariant: Border Width must remain strictly anchored
                Assert.Equal(initialWidth, border.Width);

                // Invariant: MaxHeight of textBox adapts to expanded border
                Assert.True(textBox.MaxHeight > 48 - 12);
            }
            finally
            {
                editor.Close();
            }
        }

        [AvaloniaFact]
        public void TextTool_CornerDragging_UpdatesFontSize_ScalesUpAndDown()
        {
            var textBox = new TextBox
            {
                FontSize = 20,
                Text = "Resizing Test Text",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold,
                FontFamily = FontFamily.Default
            };
            var border = new Border
            {
                Width = 200,
                Height = 80,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Child = textBox
            };

            ImageEditorWindow.FitTextBoxToBorder(border, textBox);
            double baseFontSize = textBox.FontSize;
            Assert.True(baseFontSize >= 8.0);

            // Drag corner to enlarge significantly (e.g. 600x300)
            border.Width = 600;
            border.Height = 300;
            ImageEditorWindow.FitTextBoxToBorder(border, textBox);
            Assert.True(textBox.FontSize > baseFontSize, $"Enlarging bounding box should increase font size (was {baseFontSize}, now {textBox.FontSize})");

            // Drag corner to shrink significantly (e.g. 70x30)
            border.Width = 70;
            border.Height = 30;
            ImageEditorWindow.FitTextBoxToBorder(border, textBox);
            Assert.True(textBox.FontSize < baseFontSize, $"Shrinking bounding box should decrease font size (was {baseFontSize}, now {textBox.FontSize})");
            Assert.True(textBox.FontSize >= 8.0, "Font size must not drop below minimum floor (8pt)");
        }

        [AvaloniaFact]
        public void TextTool_OutwardBorderGrowth_PreservesInnerTextBounds()
        {
            var textBox = new TextBox
            {
                FontSize = 18,
                Text = "Frame Growth Test",
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(4)
            };
            var border = new Border
            {
                Width = 200,
                Height = 100,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Child = textBox
            };

            ImageEditorWindow.SetTextBoxBoundsToBorder(border, textBox);
            double innerWidthBefore = textBox.Width;
            double innerMaxHeightBefore = textBox.MaxHeight;

            // Expand border thickness outward by delta = 4 (from 2 to 6)
            double oldT = 2;
            double newT = 6;
            double delta = newT - oldT;
            border.Width += 2 * delta;
            border.Height += 2 * delta;
            border.BorderThickness = new Thickness(newT);
            ImageEditorWindow.SetTextBoxBoundsToBorder(border, textBox);

            // Invariant: Inner text bounds must remain perfectly invariant
            Assert.Equal(innerWidthBefore, textBox.Width);
            Assert.Equal(innerMaxHeightBefore, textBox.MaxHeight);
        }

        [AvaloniaFact]
        public void TextTool_TextAlignment_PreservesCenterAcrossLTRAndRTL()
        {
            var config = new CoreConfiguration();
            var editor = new ImageEditorWindow();
            try
            {
                var textBox = new TextBox
                {
                    FontSize = 18,
                    TextWrapping = TextWrapping.Wrap
                };
                var border = new Border
                {
                    Width = 160,
                    Height = 60,
                    BorderThickness = new Thickness(2),
                    Padding = new Thickness(2),
                    Child = textBox
                };

                ImageEditorWindow.SetTextBoxBoundsToBorder(border, textBox);
                editor.AttachTextBoxBehavior(border, textBox, config);

                // English (LTR)
                textBox.Text = "English Text";
                Assert.Equal(FlowDirection.LeftToRight, textBox.FlowDirection);
                Assert.Equal(Avalonia.Media.TextAlignment.Center, textBox.TextAlignment);

                // Hebrew (RTL)
                textBox.Text = "טקסט בעברית";
                Assert.Equal(FlowDirection.RightToLeft, textBox.FlowDirection);
                Assert.Equal(Avalonia.Media.TextAlignment.Center, textBox.TextAlignment);

                // Arabic (RTL)
                textBox.Text = "مرحبا بالعالم";
                Assert.Equal(FlowDirection.RightToLeft, textBox.FlowDirection);
                Assert.Equal(Avalonia.Media.TextAlignment.Center, textBox.TextAlignment);
            }
            finally
            {
                editor.Close();
            }
        }
    }
}

