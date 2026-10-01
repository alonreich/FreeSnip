#nullable enable
using System;

namespace freesnip.editor.Services.Tools
{
    public interface IToolHandler
    {
        void OnActivated(ToolHandlerContext context);
        void OnDeactivated(ToolHandlerContext context);
        void OnPointerPressed(ToolHandlerContext context, ToolPointerEvent e);
        void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e);
        void OnPointerReleased(ToolHandlerContext context, ToolPointerEvent e);
    }
}
