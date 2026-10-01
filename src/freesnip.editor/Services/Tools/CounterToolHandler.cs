#nullable enable
using Avalonia.Input;

namespace freesnip.editor.Services.Tools
{
    public sealed class CounterToolHandler : BaseToolHandler
    {
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

            context.HideToolGhost();
            context.HideSnapHint();
            context.UpdateMagnetButtonState(altPressed);
            context.PlaceCounter(pos, context.CurrentBrush);
            context.IsDrawing = false;
        }

        public override void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e)
        {
            var pos = e.CanvasPosition;
            context.ShowToolGhost(pos);
            context.RefreshSnapTargetsList(null, pos);
            var hoveredSnap = context.SnapToNearbyTarget(pos, pos);
            context.HighlightSnapDot(hoveredSnap != pos && ToolHandlerContext.Distance(hoveredSnap, pos) <= 8.0 ? hoveredSnap : null, pos);
        }
    }
}
