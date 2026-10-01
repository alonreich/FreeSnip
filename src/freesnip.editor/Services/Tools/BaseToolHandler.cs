#nullable enable
using Avalonia.Input;

namespace freesnip.editor.Services.Tools
{
    public abstract class BaseToolHandler : IToolHandler
    {
        public virtual void OnActivated(ToolHandlerContext context) { }
        public virtual void OnDeactivated(ToolHandlerContext context) { }
        public virtual void OnPointerPressed(ToolHandlerContext context, ToolPointerEvent e) { }
        public virtual void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e) { }
        public virtual void OnPointerReleased(ToolHandlerContext context, ToolPointerEvent e) { }
    }
}
