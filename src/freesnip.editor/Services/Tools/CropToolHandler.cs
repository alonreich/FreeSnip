#nullable enable
using System;
using Avalonia;
using Avalonia.Input;
using freesnip.editor.forms;

namespace freesnip.editor.Services.Tools
{
    public sealed class CropToolHandler : BaseToolHandler
    {
        public override void OnPointerPressed(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!e.IsLeftButtonPressed) return;

            var pos = e.CanvasPosition;
            bool altPressed = e.Modifiers.HasFlag(KeyModifiers.Alt);
            if (context.CurrentImageSize.Width > 0 && context.CurrentImageSize.Height > 0)
            {
                pos = new Point(Math.Clamp(pos.X, 0, context.CurrentImageSize.Width), Math.Clamp(pos.Y, 0, context.CurrentImageSize.Height));
            }
            pos = context.ApplyCropModeToStart(pos);

            context.StartPoint = pos;
            context.IsDrawing = true;
            context.HideToolGhost();
            context.HideSnapHint();
            context.UpdateMagnetButtonState(altPressed);
            context.BeginPreviewShape(pos, context.CurrentBrush, EditorTool.Crop);
            context.CapturePointer(true);
        }

        public override void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e)
        {
            var pos = e.CanvasPosition;
            if (context.IsDrawing)
            {
                if (context.CurrentImageSize.Width > 0 && context.CurrentImageSize.Height > 0)
                {
                    pos = new Point(Math.Clamp(pos.X, 0, context.CurrentImageSize.Width), Math.Clamp(pos.Y, 0, context.CurrentImageSize.Height));
                }
                pos = context.ApplyCropModeToEnd(pos);
                context.UpdatePreviewShape(pos);
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

            var endPoint = e.CanvasPosition;
            if (context.CurrentImageSize.Width > 0 && context.CurrentImageSize.Height > 0)
            {
                endPoint = new Point(Math.Clamp(endPoint.X, 0, context.CurrentImageSize.Width), Math.Clamp(endPoint.Y, 0, context.CurrentImageSize.Height));
            }
            endPoint = context.ApplyCropModeToEnd(endPoint);

            context.RemovePreviewShape();
            context.CommitCrop(context.StartPoint, endPoint);
            context.CapturePointer(false);
        }
    }
}
