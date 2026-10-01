#nullable enable
using Avalonia;
using Avalonia.Input;

namespace freesnip.editor.Services.Tools
{
    public readonly struct ToolPointerEvent
    {
        public Point CanvasPosition { get; init; }
        public Point WindowPosition { get; init; }
        public KeyModifiers Modifiers { get; init; }
        public bool IsLeftButtonPressed { get; init; }
        public bool IsRightButtonPressed { get; init; }
        public bool IsMiddleButtonPressed { get; init; }
        public int ClickCount { get; init; }
        public bool IsCaptured { get; init; }

        public ToolPointerEvent(
            Point canvasPosition,
            Point windowPosition,
            KeyModifiers modifiers,
            bool isLeftButtonPressed,
            bool isRightButtonPressed,
            bool isMiddleButtonPressed,
            int clickCount,
            bool isCaptured)
        {
            CanvasPosition = canvasPosition;
            WindowPosition = windowPosition;
            Modifiers = modifiers;
            IsLeftButtonPressed = isLeftButtonPressed;
            IsRightButtonPressed = isRightButtonPressed;
            IsMiddleButtonPressed = isMiddleButtonPressed;
            ClickCount = clickCount;
            IsCaptured = isCaptured;
        }
    }
}
