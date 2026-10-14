#nullable enable
using System;
using Avalonia;

namespace freesnip.editor.Services
{
    public class EditorWindowFitter
    {
        public const double MinEditorWindowWidth = 880;
        public const double MinEditorWindowHeight = 630;
        public const double DefaultContentPadding = 24.0;
        public const int MaxRetries = 10;

        public bool IsPending { get; set; }
        public int Retries { get; set; }
        public PixelRect WorkingArea { get; set; }
        public double Scaling { get; set; } = 1.0;
        public double MaxWidth { get; set; }
        public double MaxHeight { get; set; }

        public void Configure(PixelRect workingArea, double scaling, double maxWidth, double maxHeight)
        {
            WorkingArea = workingArea;
            Scaling = scaling;
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
            Retries = 0;
            IsPending = true;
        }

        public static (double Width, double Height) CalculateInitialSize(
            double imageWidth,
            double imageHeight,
            double zoomFactor,
            double maxAllowedWidth,
            double maxAllowedHeight,
            double frameThickness = 1.0)
        {
            double chromeLeft = 60 + frameThickness * 2 + 18 + 12;
            double chromeRight = frameThickness * 2 + 18 + 12;
            double chromeTop = 32 + 60 + frameThickness * 2 + 18 + 12;
            double chromeBottom = 60 + frameThickness * 2 + 18 + 12;

            double targetWidth = imageWidth * zoomFactor + chromeLeft + chromeRight;
            double targetHeight = imageHeight * zoomFactor + chromeTop + chromeBottom;

            targetWidth = Math.Clamp(targetWidth, MinEditorWindowWidth, maxAllowedWidth);
            targetHeight = Math.Clamp(targetHeight, MinEditorWindowHeight, maxAllowedHeight);

            return (targetWidth, targetHeight);
        }

        public static (double Width, double Height) CalculateFitSize(
            double currentWidth,
            double currentHeight,
            double viewportWidth,
            double viewportHeight,
            double imageWidth,
            double imageHeight,
            double zoomFactor,
            double maxAllowedWidth,
            double maxAllowedHeight,
            double contentPadding = DefaultContentPadding)
        {
            double chromeWidth = currentWidth - viewportWidth;
            double chromeHeight = currentHeight - viewportHeight;

            double desiredWidth = Math.Clamp(chromeWidth + imageWidth * zoomFactor + (contentPadding * 2), MinEditorWindowWidth, maxAllowedWidth);
            double desiredHeight = Math.Clamp(chromeHeight + imageHeight * zoomFactor + (contentPadding * 2), MinEditorWindowHeight, maxAllowedHeight);

            return (desiredWidth, desiredHeight);
        }

        public static PixelPoint CalculateCenteredPosition(
            PixelRect workingArea,
            double windowWidth,
            double windowHeight,
            double scaling,
            int cascadeOffset = 0)
        {
            double centerX = workingArea.X + (workingArea.Width - (windowWidth * scaling)) / 2.0;
            double centerY = workingArea.Y + (workingArea.Height - (windowHeight * scaling)) / 2.0;

            int cascade = (cascadeOffset % 5) * 20;
            return new PixelPoint((int)(centerX + cascade), (int)(centerY + cascade));
        }

        public static PixelPoint CalculateAnchoredPosition(
            PixelRect workingArea,
            PixelPoint currentPos,
            double oldWidth,
            double oldHeight,
            double newWidth,
            double newHeight,
            double scaling)
        {
            double oldWidthPx = oldWidth * scaling;
            double oldHeightPx = oldHeight * scaling;
            double newWidthPx = newWidth * scaling;
            double newHeightPx = newHeight * scaling;

            double currentCenterXPx = currentPos.X + oldWidthPx / 2.0;
            double currentCenterYPx = currentPos.Y + oldHeightPx / 2.0;

            double targetXPx = currentCenterXPx - newWidthPx / 2.0;
            double targetYPx = currentCenterYPx - newHeightPx / 2.0;

            double minX = workingArea.X;
            double maxX = workingArea.X + Math.Max(0.0, workingArea.Width - newWidthPx);
            double minY = workingArea.Y;
            double maxY = workingArea.Y + Math.Max(0.0, workingArea.Height - newHeightPx);

            int clampedX = (int)Math.Round(Math.Clamp(targetXPx, minX, maxX));
            int clampedY = (int)Math.Round(Math.Clamp(targetYPx, minY, maxY));

            return new PixelPoint(clampedX, clampedY);
        }

        public bool TryFit(
            double currentWidth,
            double currentHeight,
            double viewportWidth,
            double viewportHeight,
            double imageWidth,
            double imageHeight,
            double zoomFactor,
            out (double Width, double Height) newSize,
            out PixelPoint newPos)
        {
            return TryFit(
                currentWidth,
                currentHeight,
                viewportWidth,
                viewportHeight,
                imageWidth,
                imageHeight,
                zoomFactor,
                currentPos: null,
                out newSize,
                out newPos);
        }

        public bool TryFit(
            double currentWidth,
            double currentHeight,
            double viewportWidth,
            double viewportHeight,
            double imageWidth,
            double imageHeight,
            double zoomFactor,
            PixelPoint? currentPos,
            out (double Width, double Height) newSize,
            out PixelPoint newPos)
        {
            return TryFit(
                currentWidth,
                currentHeight,
                viewportWidth,
                viewportHeight,
                imageWidth,
                imageHeight,
                zoomFactor,
                currentPos,
                DefaultContentPadding,
                out newSize,
                out newPos);
        }

        public bool TryFit(
            double currentWidth,
            double currentHeight,
            double viewportWidth,
            double viewportHeight,
            double imageWidth,
            double imageHeight,
            double zoomFactor,
            PixelPoint? currentPos,
            double contentPadding,
            out (double Width, double Height) newSize,
            out PixelPoint newPos)
        {
            newSize = (currentWidth, currentHeight);
            newPos = default;

            if (viewportWidth <= 1 || viewportHeight <= 1 || imageWidth <= 0 || imageHeight <= 0)
            {
                return false;
            }

            var (desiredWidth, desiredHeight) = CalculateFitSize(
                currentWidth,
                currentHeight,
                viewportWidth,
                viewportHeight,
                imageWidth,
                imageHeight,
                zoomFactor,
                MaxWidth,
                MaxHeight,
                contentPadding);

            newSize = (desiredWidth, desiredHeight);
            if (currentPos.HasValue)
            {
                newPos = CalculateAnchoredPosition(WorkingArea, currentPos.Value, currentWidth, currentHeight, desiredWidth, desiredHeight, Scaling);
            }
            else
            {
                newPos = CalculateCenteredPosition(WorkingArea, desiredWidth, desiredHeight, Scaling, 0);
            }

            bool alreadyFitted = Math.Abs(desiredWidth - currentWidth) <= 2 && Math.Abs(desiredHeight - currentHeight) <= 2;
            return !alreadyFitted;
        }
    }
}
