#nullable enable
using Avalonia.Input;

namespace freesnip.editor.Services.Tools
{
    public sealed class FreeDrawToolHandler : BaseToolHandler
    {
        public override void OnPointerPressed(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!e.IsLeftButtonPressed) return;

            var pos = e.CanvasPosition;
            bool altPressed = e.Modifiers.HasFlag(KeyModifiers.Alt);

            context.StartPoint = pos;
            context.IsDrawing = true;
            context.HideToolGhost();
            context.HideSnapHint();
            context.UpdateMagnetButtonState(altPressed);
            context.BeginFreeDraw(pos, context.CurrentBrush);
            context.CapturePointer(true);
        }

        public override void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e)
        {
            var pos = e.CanvasPosition;
            if (context.IsDrawing)
            {
                context.AddFreeDrawPoint(pos);
            }
            else
            {
                context.ShowToolGhost(pos);
            }
        }

        public override void OnPointerReleased(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!context.IsDrawing) return;

            context.IsDrawing = false;
            context.EndFreeDraw();
            context.CapturePointer(false);
        }
    }
}
