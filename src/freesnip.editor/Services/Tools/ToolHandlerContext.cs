#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using freesnip.editor.forms;

namespace freesnip.editor.Services.Tools
{
    public interface IToolContextBridge
    {
        Canvas? Canvas { get; }
        Size CurrentImageSize { get; }
        double ZoomFactor { get; }
        EditorHistoryManager History { get; }
        IBrush CurrentBrush { get; }
        double CurrentThickness { get; }
        string PendingEmoji { get; }
        int CounterValue { get; set; }
        bool IsFillMode { get; }
        bool GetToolFillMode(EditorTool tool);
        bool IsDrawing { get; set; }
        Point StartPoint { get; set; }

        void CaptureUndoCheckpoint(bool cloneImage = false);
        void SetEditorCursor(Cursor cursor);
        void ShowToolGhost(Point pos);
        void HideToolGhost();
        void ShowSnapGuides();
        void HideSnapGuides();
        void ShowSnapHint(Point pos, bool altPressed);
        void HideSnapHint();
        void ShowToast(string message);
        void CapturePointer(bool capture);
        void UpdateMagnetButtonState(bool altPressed);

        Point SnapToNearbyTarget(Point target, Point fallback);
        Point ClampRectDrawEnd(Point end);
        Point ApplyVectorConstraints(Point current, Point anchor, KeyModifiers modifiers, bool allowTargetSnap);
        void TriggerSnapGlowEffect(Point point);
        void RefreshSnapTargetsList(Control? exclude, Point currentPos);
        void HighlightSnapDot(Point? snappedPoint, Point currentPos);
        void UpdateVectorInfo(Point start, Point end, bool altPressed);
        void HideVectorInfo();

        void BeginPreviewShape(Point start, IBrush brush, EditorTool tool);
        void UpdatePreviewShape(Point end);
        void RemovePreviewShape();
        void CommitShape(Point start, Point end, EditorTool tool);
        void AddAnnotation(Control control);
        void FinalizeSelectedPasteObject();
        void ClearSelection();

        void PlaceCounter(Point pos, IBrush brush);
        void PlaceEmoji(Point pos, string emoji);
        void BeginFreeDraw(Point start, IBrush brush);
        void AddFreeDrawPoint(Point pos);
        void EndFreeDraw();
        Point ApplyCropModeToStart(Point pt);
        Point ApplyCropModeToEnd(Point pt);
        void CommitCrop(Point start, Point end);
    }

    public class ToolHandlerContext
    {
        private readonly IToolContextBridge _bridge;

        public ToolHandlerContext(IToolContextBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public Canvas? Canvas => _bridge.Canvas;
        public Size CurrentImageSize => _bridge.CurrentImageSize;
        public double ZoomFactor => _bridge.ZoomFactor;
        public EditorHistoryManager History => _bridge.History;
        public IBrush CurrentBrush => _bridge.CurrentBrush;
        public double CurrentThickness => _bridge.CurrentThickness;
        public string PendingEmoji => _bridge.PendingEmoji;
        public int CounterValue
        {
            get => _bridge.CounterValue;
            set => _bridge.CounterValue = value;
        }
        public bool IsFillMode => _bridge.IsFillMode;
        public bool GetToolFillMode(EditorTool tool) => _bridge.GetToolFillMode(tool);

        public bool IsDrawing
        {
            get => _bridge.IsDrawing;
            set => _bridge.IsDrawing = value;
        }

        public Point StartPoint
        {
            get => _bridge.StartPoint;
            set => _bridge.StartPoint = value;
        }

        public void CaptureUndoCheckpoint(bool cloneImage = false) => _bridge.CaptureUndoCheckpoint(cloneImage);
        public void SetEditorCursor(Cursor cursor) => _bridge.SetEditorCursor(cursor);
        public void ShowToolGhost(Point pos) => _bridge.ShowToolGhost(pos);
        public void HideToolGhost() => _bridge.HideToolGhost();
        public void ShowSnapGuides() => _bridge.ShowSnapGuides();
        public void HideSnapGuides() => _bridge.HideSnapGuides();
        public void ShowSnapHint(Point pos, bool altPressed) => _bridge.ShowSnapHint(pos, altPressed);
        public void HideSnapHint() => _bridge.HideSnapHint();
        public void ShowToast(string message) => _bridge.ShowToast(message);
        public void CapturePointer(bool capture) => _bridge.CapturePointer(capture);
        public void UpdateMagnetButtonState(bool altPressed) => _bridge.UpdateMagnetButtonState(altPressed);

        public Point SnapToNearbyTarget(Point target, Point fallback) => _bridge.SnapToNearbyTarget(target, fallback);
        public Point ClampRectDrawEnd(Point end) => _bridge.ClampRectDrawEnd(end);
        public Point ApplyVectorConstraints(Point current, Point anchor, KeyModifiers modifiers, bool allowTargetSnap)
            => _bridge.ApplyVectorConstraints(current, anchor, modifiers, allowTargetSnap);
        public void TriggerSnapGlowEffect(Point point) => _bridge.TriggerSnapGlowEffect(point);
        public void RefreshSnapTargetsList(Control? exclude, Point currentPos) => _bridge.RefreshSnapTargetsList(exclude, currentPos);
        public void HighlightSnapDot(Point? snappedPoint, Point currentPos) => _bridge.HighlightSnapDot(snappedPoint, currentPos);
        public void UpdateVectorInfo(Point start, Point end, bool altPressed) => _bridge.UpdateVectorInfo(start, end, altPressed);
        public void HideVectorInfo() => _bridge.HideVectorInfo();

        public void BeginPreviewShape(Point start, IBrush brush, EditorTool tool) => _bridge.BeginPreviewShape(start, brush, tool);
        public void UpdatePreviewShape(Point end) => _bridge.UpdatePreviewShape(end);
        public void RemovePreviewShape() => _bridge.RemovePreviewShape();
        public void CommitShape(Point start, Point end, EditorTool tool) => _bridge.CommitShape(start, end, tool);
        public void AddAnnotation(Control control) => _bridge.AddAnnotation(control);
        public void FinalizeSelectedPasteObject() => _bridge.FinalizeSelectedPasteObject();
        public void ClearSelection() => _bridge.ClearSelection();

        public void PlaceCounter(Point pos, IBrush brush) => _bridge.PlaceCounter(pos, brush);
        public void PlaceEmoji(Point pos, string emoji) => _bridge.PlaceEmoji(pos, emoji);
        public void BeginFreeDraw(Point start, IBrush brush) => _bridge.BeginFreeDraw(start, brush);
        public void AddFreeDrawPoint(Point pos) => _bridge.AddFreeDrawPoint(pos);
        public void EndFreeDraw() => _bridge.EndFreeDraw();
        public Point ApplyCropModeToStart(Point pt) => _bridge.ApplyCropModeToStart(pt);
        public Point ApplyCropModeToEnd(Point pt) => _bridge.ApplyCropModeToEnd(pt);
        public void CommitCrop(Point start, Point end) => _bridge.CommitCrop(start, end);

        public static double Distance(Point a, Point b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }
}
