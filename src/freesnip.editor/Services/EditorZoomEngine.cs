#nullable enable
using System;
using Avalonia;
using Avalonia.Threading;

namespace freesnip.editor.Services
{
    public sealed class EditorZoomEngine : IDisposable
    {
        public const double OverscrollPadding = 64.0;
        public const double ZoomStepMultiplier = 1.08;
        public const double MinZoomFactor = 0.10;
        public const double MaxZoomFactor = 6.0;

        private double _zoomFactor = 1.0;
        private double _targetZoom = 1.0;
        private Point? _zoomFocalViewportPoint;
        private Point? _zoomFocalUnscaledPoint;
        private DispatcherTimer? _smoothZoomTimer;
        private readonly Action? _onZoomChanged;

        public EditorZoomEngine(Action? onZoomChanged = null)
        {
            _onZoomChanged = onZoomChanged;
        }

        public double ZoomFactor
        {
            get => _zoomFactor;
            set => _zoomFactor = Math.Clamp(value, MinZoomFactor, MaxZoomFactor);
        }

        public double TargetZoom
        {
            get => _targetZoom;
            set => _targetZoom = Math.Clamp(value, MinZoomFactor, MaxZoomFactor);
        }
        public Point? FocalViewportPoint => _zoomFocalViewportPoint;
        public Point? FocalUnscaledPoint => _zoomFocalUnscaledPoint;
        public bool IsAnimating => _smoothZoomTimer?.IsEnabled ?? false;

        public void ClearFocalPoints()
        {
            _zoomFocalViewportPoint = null;
            _zoomFocalUnscaledPoint = null;
        }

        public void SetFocalPoint(
            Point pointerInScroller,
            double scrollOffsetX,
            double scrollOffsetY,
            double viewportWidth,
            double viewportHeight,
            double currentPadX,
            double currentPadY,
            double imageWidth,
            double imageHeight)
        {
            if (imageWidth <= 0 || imageHeight <= 0) return;

            CalculateFocalPoints(
                pointerInScroller,
                scrollOffsetX,
                scrollOffsetY,
                _zoomFactor,
                imageWidth,
                imageHeight,
                viewportWidth,
                viewportHeight,
                currentPadX,
                currentPadY,
                out var vpPoint,
                out var unscaledPoint);

            _zoomFocalViewportPoint = vpPoint;
            _zoomFocalUnscaledPoint = unscaledPoint;
        }

        public void SetFocalPointToCenter(
            double scrollOffsetX,
            double scrollOffsetY,
            double viewportWidth,
            double viewportHeight,
            double currentPadX,
            double currentPadY,
            double imageWidth,
            double imageHeight)
        {
            double vpX = viewportWidth / 2.0;
            double vpY = viewportHeight / 2.0;
            SetFocalPoint(new Point(vpX, vpY), scrollOffsetX, scrollOffsetY, viewportWidth, viewportHeight, currentPadX, currentPadY, imageWidth, imageHeight);
        }

        public void SmoothZoomTowards(double requestedZoom)
        {
            _targetZoom = Math.Clamp(requestedZoom, MinZoomFactor, MaxZoomFactor);
            if (Math.Abs(_targetZoom - _zoomFactor) < 0.001) return;

            if (_smoothZoomTimer == null)
            {
                _smoothZoomTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(10),
                    DispatcherPriority.Render,
                    OnSmoothZoomTick);
            }

            if (!_smoothZoomTimer.IsEnabled)
            {
                _smoothZoomTimer.Start();
            }
        }

        public void StopAnimation()
        {
            _smoothZoomTimer?.Stop();
        }

        public void Dispose()
        {
            if (_smoothZoomTimer != null)
            {
                _smoothZoomTimer.Stop();
                _smoothZoomTimer.Tick -= OnSmoothZoomTick;
                _smoothZoomTimer = null;
            }
        }

        private void OnSmoothZoomTick(object? sender, EventArgs e)
        {
            if (StepInterpolation(_zoomFactor, _targetZoom, out double nextZoom, out bool completed))
            {
                _zoomFactor = nextZoom;
                if (completed)
                {
                    _smoothZoomTimer?.Stop();
                }
            }

            _onZoomChanged?.Invoke();
        }

        public static bool StepInterpolation(double current, double target, out double next, out bool completed)
        {
            double diff = target - current;
            if (Math.Abs(diff) < 0.0005)
            {
                next = target;
                completed = true;
                return true;
            }

            next = current + diff * 0.20;
            completed = false;
            return true;
        }

        public static void CalculateViewportPadding(
            double zoom,
            double imageWidth,
            double imageHeight,
            double viewportWidth,
            double viewportHeight,
            double windowWidth,
            double windowHeight,
            out double padX,
            out double padY,
            out double scaledW,
            out double scaledH)
        {
            scaledW = Math.Max(0, imageWidth) * zoom;
            scaledH = Math.Max(0, imageHeight) * zoom;

            double vpW = viewportWidth;
            double vpH = viewportHeight;

            if (vpW <= 1 || vpH <= 1)
            {
                double estW = Math.Max(100, windowWidth - 60);
                double estH = Math.Max(100, windowHeight - 152);
                if (estW > 1 && estH > 1)
                {
                    vpW = estW;
                    vpH = estH;
                }
                else
                {
                    padX = 0;
                    padY = 0;
                    return;
                }
            }

            padX = (scaledW < vpW)
                ? Math.Max(0, (vpW - scaledW) / 2.0)
                : OverscrollPadding;

            padY = (scaledH < vpH)
                ? Math.Max(0, (vpH - scaledH) / 2.0)
                : OverscrollPadding;
        }

        public static void CalculateFocalPoints(
            Point pointerInScroller,
            double scrollOffsetX,
            double scrollOffsetY,
            double zoom,
            double imageWidth,
            double imageHeight,
            double viewportWidth,
            double viewportHeight,
            double currentPadX,
            double currentPadY,
            out Point focalViewportPoint,
            out Point focalUnscaledPoint)
        {
            double vpX = Math.Clamp(pointerInScroller.X, 0, Math.Max(0, viewportWidth));
            double vpY = Math.Clamp(pointerInScroller.Y, 0, Math.Max(0, viewportHeight));

            double safeZoom = Math.Max(0.001, zoom);
            double unscaledX = (scrollOffsetX + vpX - currentPadX) / safeZoom;
            double unscaledY = (scrollOffsetY + vpY - currentPadY) / safeZoom;

            unscaledX = Math.Clamp(unscaledX, 0, Math.Max(0, imageWidth));
            unscaledY = Math.Clamp(unscaledY, 0, Math.Max(0, imageHeight));

            focalViewportPoint = new Point(vpX, vpY);
            focalUnscaledPoint = new Point(unscaledX, unscaledY);
        }

        public static Vector CalculateDesiredOffset(
            double zoom,
            double padX,
            double padY,
            double totalW,
            double totalH,
            double viewportWidth,
            double viewportHeight,
            Point? focalVp,
            Point? focalUnscaled)
        {
            double maxOffsetX = Math.Max(0, totalW - viewportWidth);
            double maxOffsetY = Math.Max(0, totalH - viewportHeight);

            if (maxOffsetX <= 0 && maxOffsetY <= 0)
            {
                return new Vector(0, 0);
            }

            if (focalVp.HasValue && focalUnscaled.HasValue)
            {
                var vp = focalVp.Value;
                var imgPt = focalUnscaled.Value;

                double desiredOffsetX = (imgPt.X * zoom) + padX - vp.X;
                double desiredOffsetY = (imgPt.Y * zoom) + padY - vp.Y;

                return new Vector(Math.Clamp(desiredOffsetX, 0, maxOffsetX), Math.Clamp(desiredOffsetY, 0, maxOffsetY));
            }
            else
            {
                double targetX = (totalW - viewportWidth) / 2.0;
                double targetY = (totalH - viewportHeight) / 2.0;
                return new Vector(Math.Clamp(targetX, 0, maxOffsetX), Math.Clamp(targetY, 0, maxOffsetY));
            }
        }
    }
}
