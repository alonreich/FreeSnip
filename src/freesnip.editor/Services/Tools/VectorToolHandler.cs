#nullable enable
using Avalonia;
using Avalonia.Input;
using freesnip.editor.forms;

namespace freesnip.editor.Services.Tools
{
    public abstract class VectorToolHandler : BaseToolHandler
    {
        public abstract EditorTool Tool { get; }

        public override void OnPointerPressed(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!e.IsLeftButtonPressed) return;

            var pos = e.CanvasPosition;
            bool altPressed = e.Modifiers.HasFlag(KeyModifiers.Alt);
            if (!altPressed)
            {
                var nearbyDot = context.SnapToNearbyTarget(pos, pos);
                if (ToolHandlerContext.Distance(nearbyDot, pos) <= 8.0 && nearbyDot != pos)
                {
                    pos = nearbyDot;
                    context.TriggerSnapGlowEffect(nearbyDot);
                }
            }

            context.StartPoint = pos;
            context.IsDrawing = true;
            context.HideToolGhost();
            context.HideSnapHint();
            context.UpdateMagnetButtonState(altPressed);
            context.BeginPreviewShape(pos, context.CurrentBrush, Tool);
            context.CapturePointer(true);
        }

        public override void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e)
        {
            var pos = e.CanvasPosition;
            bool altPressed = e.Modifiers.HasFlag(KeyModifiers.Alt);

            if (context.IsDrawing)
            {
                context.UpdateMagnetButtonState(altPressed);
                context.RefreshSnapTargetsList(null, pos);

                Point? snappedPoint = null;
                var t = context.SnapToNearbyTarget(pos, pos);
                if (!altPressed && t != pos && ToolHandlerContext.Distance(t, pos) <= 8.0)
                {
                    snappedPoint = t;
                }

                var previewEnd = context.ApplyVectorConstraints(pos, context.StartPoint, e.Modifiers, allowTargetSnap: !altPressed);
                previewEnd = context.ClampRectDrawEnd(previewEnd);
                context.UpdatePreviewShape(previewEnd);

                context.HideSnapGuides();
                context.HideSnapHint();
                context.UpdateVectorInfo(context.StartPoint, previewEnd, altPressed);
                context.HighlightSnapDot(snappedPoint, pos);
            }
            else
            {
                context.ShowToolGhost(pos);
                context.RefreshSnapTargetsList(null, pos);
                var hoveredSnap = context.SnapToNearbyTarget(pos, pos);
                context.HighlightSnapDot(hoveredSnap != pos && ToolHandlerContext.Distance(hoveredSnap, pos) <= 8.0 ? hoveredSnap : null, pos);
            }
        }

        public override void OnPointerReleased(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!context.IsDrawing) return;
            context.IsDrawing = false;

            var endPoint = e.CanvasPosition;
            bool altPressed = e.Modifiers.HasFlag(KeyModifiers.Alt);
            endPoint = context.ApplyVectorConstraints(endPoint, context.StartPoint, e.Modifiers, allowTargetSnap: !altPressed);
            endPoint = context.ClampRectDrawEnd(endPoint);

            context.RemovePreviewShape();
            context.CommitShape(context.StartPoint, endPoint, Tool);
            context.CapturePointer(false);
        }
    }
}
