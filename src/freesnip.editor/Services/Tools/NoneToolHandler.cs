#nullable enable
namespace freesnip.editor.Services.Tools
{
    public sealed class NoneToolHandler : BaseToolHandler
    {
        public override void OnActivated(ToolHandlerContext context)
        {
            context.HideToolGhost();
            context.HideSnapGuides();
            context.HideVectorInfo();
        }

        public override void OnPointerMoved(ToolHandlerContext context, ToolPointerEvent e)
        {
            if (!context.IsDrawing)
            {
                context.HideToolGhost();
                context.HideSnapGuides();
                context.HideVectorInfo();
            }
        }
    }
}
