using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using log4net;
using freesnip.foundation.core;
using freesnip.native.foundation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;

namespace freesnip.helpers
{
    internal sealed class ScrollCaptureRecorder : IAsyncDisposable
    {
        private static readonly ILog Log = LogHelper.GetLogger(typeof(ScrollCaptureRecorder));
        private readonly RECT _target;
        private readonly Channel<Image<Bgra32>> _frames;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ScrollFrameStitcher _stitcher = new ScrollFrameStitcher();
        private Task _producer;
        private Task _consumer;
        private volatile bool _trackingFailed;
        private int _rejectedFrames;

        public ScrollCaptureRecorder(RECT target)
        {
            _target = target.Normalize();
            _frames = Channel.CreateBounded<Image<Bgra32>>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        }

        public const int MaxSegments = ScrollFrameStitcher.MaxSegments;
        public int AcceptedFrames => _stitcher.AcceptedFrames;
        public double EstimatedScreens => _stitcher.EstimatedScreens;
        public bool IsSegmentCeilingReached => _stitcher.IsSegmentCeilingReached;
        public int TotalCapturedHeight => _stitcher.TotalCapturedHeight;
        public long CurrentCompositePixels => _stitcher.CurrentCompositePixels;
        public bool IsPaused { get; private set; }
        public event Action SegmentCeilingReached;

        public void Pause()
        {
            if (!IsPaused)
            {
                IsPaused = true;
                Log.Info("Scroll capture paused: segment ceiling reached.");
                SegmentCeilingReached?.Invoke();
            }
        }

        public void Resume()
        {
            IsPaused = false;
        }

        public void Start()
        {
            _producer = Task.Run(ProduceAsync);
            _consumer = Task.Run(ConsumeAsync);
        }

        public async Task<Image<Bgra32>> FinishAsync(IProgress<double> progress = null)
        {
            _cts.Cancel();
            _frames.Writer.TryComplete();
            await WaitForTasksAsync().ConfigureAwait(false);
            if (_stitcher.AcceptedFrames < 1)
            {
                return null;
            }

            return await Task.Run(() => _stitcher.BuildImage(progress)).ConfigureAwait(false);
        }

        public async Task CancelAsync()
        {
            _cts.Cancel();
            _frames.Writer.TryComplete();
            await WaitForTasksAsync().ConfigureAwait(false);
            DrainPendingFrames();
        }

        public async ValueTask DisposeAsync()
        {
            await CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _stitcher.Dispose();
        }

        private void DrainPendingFrames()
        {
            while (_frames.Reader.TryRead(out var frame))
            {
                frame?.Dispose();
            }
        }

        private async Task ProduceAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested && !_trackingFailed)
                {
                    if (IsPaused || _stitcher.IsSegmentCeilingReached)
                    {
                        await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    Image<Bgra32> frame = NativeCapture.CaptureRegion(_target, false);
                    if (frame != null)
                    {
                        try
                        {
                            await _frames.Writer.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            frame.Dispose();
                            throw;
                        }
                    }

                    await Task.Delay(75, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("Scroll capture producer failed.", ex);
                _trackingFailed = true;
            }
            finally
            {
                _frames.Writer.TryComplete();
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (Image<Bgra32> frame in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (_stitcher.IsSegmentCeilingReached)
                    {
                        Pause();
                        frame.Dispose();
                        continue;
                    }

                    ScrollFrameStatus status = _stitcher.AddFrame(frame);
                    if (status == ScrollFrameStatus.Rejected)
                    {
                        _rejectedFrames++;
                    }
                    else if (status == ScrollFrameStatus.Accepted)
                    {
                        _rejectedFrames = 0;
                    }

                    if (_stitcher.IsSegmentCeilingReached)
                    {
                        Pause();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("Scroll capture consumer failed.", ex);
            }
            finally
            {
                DrainPendingFrames();
            }
        }

        private async Task WaitForTasksAsync()
        {
            try { if (_producer != null) await _producer.ConfigureAwait(false); } catch { }
            try { if (_consumer != null) await _consumer.ConfigureAwait(false); } catch { }
        }
    }

    internal enum ScrollFrameStatus
    {
        Accepted,
        Duplicate,
        Rejected
    }

    internal sealed class ScrollFrameStitcher : IDisposable
    {
        private static readonly ILog Log = LogHelper.GetLogger(typeof(ScrollFrameStitcher));
        private const int MinMovementPixels = 2;
        private const double MaxAverageDiff = 32.0;
        private const double MaxRefinedDiff = 32.0;
        private const int BandHeightPixels = 257;
        public const long DefaultMaxCompositePixels = 180L * 1024L * 1024L;
        public const int DefaultMaxSegments = int.MaxValue;

        public const long MaxCompositePixels = DefaultMaxCompositePixels;
        public const int MaxSegments = DefaultMaxSegments;

        public long MaxCompositePixelsLimit { get; set; } = DefaultMaxCompositePixels;
        public int MaxSegmentsLimit { get; set; } = DefaultMaxSegments;

        private readonly List<ScrollSegment> _segments = new List<ScrollSegment>();
        private SampleFrame _previousSample;
        private byte[] _previousBand;
        private int _bandWidth;
        private int _bandHeight;
        private int _offsetX;
        private int _offsetY;
        private int _frameWidth;
        private int _frameHeight;
        private bool _disposed;

        private Image<Bgra32> _firstFrame;
        private Image<Bgra32> _lastFrame;
        private bool _viewportDetected;
        private Rectangle _viewport;

        public int AcceptedFrames { get; private set; }
        public int SegmentCount => _segments.Count;
        public long CurrentCompositePixels
        {
            get
            {
                if (_frameWidth <= 0 || _frameHeight <= 0) return 0;
                int scrollDist = Math.Max(Math.Abs(_offsetY), Math.Abs(_offsetX));
                long totalH = (long)_frameHeight + scrollDist;
                return (long)_frameWidth * totalH;
            }
        }
        public bool IsMemoryLimitReached => CurrentCompositePixels >= MaxCompositePixelsLimit;
        public bool IsSegmentCeilingReached => IsMemoryLimitReached || _segments.Count >= MaxSegmentsLimit;
        public int TotalCapturedHeight
        {
            get
            {
                if (_frameHeight <= 0) return 0;
                int scrollDist = Math.Max(Math.Abs(_offsetY), Math.Abs(_offsetX));
                return _frameHeight + scrollDist;
            }
        }
        internal IReadOnlyList<ScrollSegment> Segments => _segments;

        public ScrollFrameStitcher(int maxSegments = DefaultMaxSegments, long maxCompositePixels = DefaultMaxCompositePixels)
        {
            MaxSegmentsLimit = maxSegments;
            MaxCompositePixelsLimit = maxCompositePixels;
        }

        public double EstimatedScreens
        {
            get
            {
                if (_frameHeight <= 0) return 1.0;
                int scrollDist = Math.Max(Math.Abs(_offsetY), Math.Abs(_offsetX));
                return 1.0 + ((double)scrollDist / _frameHeight);
            }
        }

        public ScrollFrameStatus AddFrame(Image<Bgra32> frame)
        {
            if (frame == null) return ScrollFrameStatus.Rejected;

            try
            {
                if (IsSegmentCeilingReached)
                {
                    return ScrollFrameStatus.Rejected;
                }

                if (AcceptedFrames == 0)
                {
                    _frameWidth = frame.Width;
                    _frameHeight = frame.Height;
                    _firstFrame = frame.Clone(x => { });
                    _lastFrame = frame.Clone(x => { });
                    Rectangle initialArea = GetCentralContentArea(frame.Width, frame.Height);
                    _previousSample = SampleFrame.Create(frame, initialArea);
                    _previousBand = BuildBand(frame, initialArea);
                    AcceptedFrames = 1;
                    return ScrollFrameStatus.Accepted;
                }

                if (frame.Width != _frameWidth || frame.Height != _frameHeight) return ScrollFrameStatus.Rejected;

                Rectangle safeSearchArea = _viewportDetected ? _viewport : GetCentralContentArea(_frameWidth, _frameHeight);
                SampleFrame currentSample = SampleFrame.Create(frame, safeSearchArea);
                MovementEstimate estimate = EstimateMovement(_previousSample, currentSample);

                if (!estimate.IsReliable)
                {
                    currentSample.Dispose();
                    return ScrollFrameStatus.Rejected;
                }

                byte[] currentBand = BuildBand(frame, safeSearchArea);
                estimate = RefineMovement(_previousBand, currentBand, estimate);
                
                if (!estimate.IsReliable)
                {
                    currentSample.Dispose();
                    return ScrollFrameStatus.Rejected;
                }

                if (Math.Abs(estimate.DeltaX) < MinMovementPixels && Math.Abs(estimate.DeltaY) < MinMovementPixels)
                {
                    currentSample.Dispose();
                    return ScrollFrameStatus.Duplicate;
                }

                if (!_viewportDetected)
                {
                    _viewport = DetectViewport(_firstFrame, frame);
                    _viewportDetected = true;
                    _segments.Clear();
                    _segments.Add(new ScrollSegment(_firstFrame.Clone(ctx => ctx.Crop(_viewport)), 0, 0));
                    
                    currentSample.Dispose();
                    currentSample = SampleFrame.Create(frame, _viewport);
                    currentBand = BuildBand(frame, _viewport);
                    
                    _previousSample.Dispose();
                    _previousSample = SampleFrame.Create(_firstFrame, _viewport);
                    _previousBand = BuildBand(_firstFrame, _viewport);
                    estimate = EstimateMovement(_previousSample, currentSample);
                    estimate = RefineMovement(_previousBand, currentBand, estimate);
                }

                _offsetX += estimate.DeltaX;
                _offsetY += estimate.DeltaY;
                AddVisibleStrips(frame, _offsetX, _offsetY, estimate.DeltaX, estimate.DeltaY);

                _previousSample.Dispose();
                _previousSample = currentSample;
                _previousBand = currentBand;

                _lastFrame?.Dispose();
                _lastFrame = frame.Clone(x => { });

                AcceptedFrames++;
                return ScrollFrameStatus.Accepted;
            }
            finally
            {
                frame.Dispose();
            }
        }

        private Rectangle DetectViewport(Image<Bgra32> a, Image<Bgra32> b)
        {
            int width = a.Width;
            int height = a.Height;

            bool RowMatches(int y)
            {
                long diff = 0;
                int step = Math.Max(1, width / 120);
                int sampled = 0;
                a.ProcessPixelRows(b, (aa, bb) =>
                {
                    var spanA = aa.GetRowSpan(y);
                    var spanB = bb.GetRowSpan(y);
                    for (int x = 0; x < width; x += step)
                    {
                        int r = Math.Abs(spanA[x].R - spanB[x].R);
                        int g = Math.Abs(spanA[x].G - spanB[x].G);
                        int bl = Math.Abs(spanA[x].B - spanB[x].B);
                        diff += (r + g + bl) / 3;
                        sampled++;
                    }
                });
                return sampled == 0 || diff < sampled * 3;
            }

            int top = 0;
            for (int y = 0; y < height - 4; y++)
            {
                if (!RowMatches(y) && !RowMatches(y + 1) && !RowMatches(y + 2))
                {
                    top = y;
                    break;
                }
            }

            int bottom = height - 1;
            for (int y = height - 1; y > top + 4; y--)
            {
                if (!RowMatches(y) && !RowMatches(y - 1) && !RowMatches(y - 2))
                {
                    bottom = y;
                    break;
                }
            }

            if (bottom <= top + 32)
            {
                top = 0;
                bottom = height - 1;
            }
            else
            {
                if (top > height * 0.60) top = 0;
                if (bottom < height * 0.30) bottom = height - 1;
            }

            int left = 0;
            int right = width - 1;

            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        private static Rectangle GetCentralContentArea(int width, int height)
        {
            int topMargin = Math.Max(0, (int)(height * 0.15));
            int bottomMargin = Math.Max(0, (int)(height * 0.08));
            int sideMargin = Math.Max(0, (int)(width * 0.08));
            int contentWidth = Math.Max(16, width - sideMargin * 2);
            int contentHeight = Math.Max(16, height - topMargin - bottomMargin);
            return new Rectangle(sideMargin, topMargin, contentWidth, contentHeight);
        }

        public Image<Bgra32> BuildImage(IProgress<double> progress = null)
        {
            if (AcceptedFrames < 1 || _firstFrame == null) return null;
            if (_segments.Count == 0 || !_viewportDetected)
            {
                return _firstFrame.Clone(x => { });
            }
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var segment in _segments)
            {
                minX = Math.Min(minX, segment.X);
                minY = Math.Min(minY, segment.Y);
                maxX = Math.Max(maxX, segment.X + segment.Image.Width);
                maxY = Math.Max(maxY, segment.Y + segment.Image.Height);
            }

            int vpW = maxX - minX;
            int vpH = maxY - minY;
            int width = _frameWidth;
            int height = vpH + _viewport.Top + (_frameHeight - _viewport.Bottom);

            if (width <= 0 || height <= 0) return null;

            long totalPixels = (long)width * height;
            long pixelCeiling = MaxCompositePixelsLimit;
            if (totalPixels > pixelCeiling)
            {
                int clampedHeight = (int)(pixelCeiling / width);
                if (clampedHeight < _frameHeight) clampedHeight = _frameHeight;
                Log.Warn($"Scroll capture composite dimension ({width}x{height}, {totalPixels} pixels) exceeds MaxCompositePixels ({pixelCeiling}). Clamping composite height to {clampedHeight} pixels.");
                height = clampedHeight;
            }

            var result = new Image<Bgra32>(width, height);
            Image<Bgra32> header = null;
            Image<Bgra32> footer = null;
            Image<Bgra32> leftBar = null;
            Image<Bgra32> rightBar = null;

            try
            {
                if (_viewport.Top > 0 && _frameWidth > 0 && height > 0)
                {
                    int headerH = Math.Min(_viewport.Top, height);
                    header = _firstFrame.Clone(c => c.Crop(new Rectangle(0, 0, _frameWidth, headerH)));
                    result.Mutate(ctx => ctx.DrawImage(header, new Point(0, 0), 1f));
                }

                int footerHeight = _frameHeight - _viewport.Bottom;
                if (footerHeight > 0 && _frameWidth > 0 && height >= footerHeight)
                {
                    footer = _lastFrame.Clone(c => c.Crop(new Rectangle(0, _viewport.Bottom, _frameWidth, footerHeight)));
                }

                if (_viewport.Left > 0 && _viewport.Height > 0)
                {
                    leftBar = _firstFrame.Clone(c => c.Crop(new Rectangle(0, _viewport.Top, _viewport.Left, _viewport.Height)));
                }

                int rightBarWidth = _frameWidth - _viewport.Right;
                if (rightBarWidth > 0 && _viewport.Height > 0)
                {
                    rightBar = _firstFrame.Clone(c => c.Crop(new Rectangle(_viewport.Right, _viewport.Top, rightBarWidth, _viewport.Height)));
                }

                int currentY = Math.Max(0, _viewport.Top);
                int bottomLimit = footer != null ? Math.Max(0, height - footer.Height) : height;
                int stepY = Math.Max(1, _viewport.Height);

                while (currentY < bottomLimit)
                {
                    if (leftBar != null)
                    {
                        result.Mutate(ctx => ctx.DrawImage(leftBar, new Point(0, currentY), 1f));
                    }
                    if (rightBar != null)
                    {
                        result.Mutate(ctx => ctx.DrawImage(rightBar, new Point(_viewport.Right, currentY), 1f));
                    }
                    currentY += stepY;
                }

                int count = 0;
                foreach (var segment in _segments)
                {
                    int targetX = segment.X - minX + _viewport.Left;
                    int targetY = segment.Y - minY + _viewport.Top;
                    if (targetY < height && targetX < width && targetY + segment.Image.Height > 0 && targetX + segment.Image.Width > 0)
                    {
                        result.Mutate(ctx => ctx.DrawImage(segment.Image, new Point(targetX, targetY), 1f));
                    }
                    count++;
                    progress?.Report((double)count / _segments.Count);
                }

                if (footer != null)
                {
                    result.Mutate(ctx => ctx.DrawImage(footer, new Point(0, height - footer.Height), 1f));
                }

                return result;
            }
            finally
            {
                header?.Dispose();
                footer?.Dispose();
                leftBar?.Dispose();
                rightBar?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _previousSample?.Dispose();
            _firstFrame?.Dispose();
            _lastFrame?.Dispose();
            foreach (var segment in _segments) segment.Image.Dispose();
            _segments.Clear();
        }

        private void AddVisibleStrips(Image<Bgra32> frame, int offsetX, int offsetY, int deltaX, int deltaY)
        {
            int absY = Math.Abs(deltaY);
            int absX = Math.Abs(deltaX);

            if (absY >= absX && absY >= MinMovementPixels)
            {
                int h = Math.Min(absY, _viewport.Height);

                // Memory-bounded streaming check: clamp strip height if approaching limit
                if (_frameWidth > 0 && MaxCompositePixelsLimit > 0)
                {
                    long maxAllowedHeight = MaxCompositePixelsLimit / _frameWidth;
                    long previousHeight = (long)_frameHeight + Math.Max(Math.Abs(offsetY - deltaY), Math.Abs(offsetX - deltaX));
                    if (previousHeight >= maxAllowedHeight)
                    {
                        return;
                    }
                    long remainingHeight = maxAllowedHeight - previousHeight;
                    if (h > remainingHeight)
                    {
                        h = (int)remainingHeight;
                    }
                }

                if (h <= 0) return;

                Rectangle crop = deltaY > 0
                    ? new Rectangle(_viewport.X, _viewport.Bottom - h, _viewport.Width, h)
                    : new Rectangle(_viewport.X, _viewport.Top, _viewport.Width, h);
                int y = deltaY > 0 ? offsetY + _viewport.Height - h : offsetY;
                _segments.Add(new ScrollSegment(frame.Clone(ctx => ctx.Crop(crop)), offsetX, y));
            }
            else if (absX > absY && absX >= MinMovementPixels)
            {
                int w = Math.Min(absX, _viewport.Width);
                if (w <= 0) return;

                Rectangle crop = deltaX > 0
                    ? new Rectangle(_viewport.Right - w, _viewport.Top, w, _viewport.Height)
                    : new Rectangle(_viewport.X, _viewport.Top, w, _viewport.Height);
                int x = deltaX > 0 ? offsetX + _viewport.Width - w : offsetX;
                _segments.Add(new ScrollSegment(frame.Clone(ctx => ctx.Crop(crop)), x, offsetY));
            }
        }

        private static MovementEstimate EstimateMovement(SampleFrame previous, SampleFrame current)
        {
            if (previous == null || current == null || previous.Width != current.Width || previous.Height != current.Height)
                return MovementEstimate.Failed;

            int width = previous.Width;
            int height = previous.Height;
            int maxDx = Math.Max(1, Math.Min(14, (int)(width * 0.12)));
            int maxDy = Math.Max(1, (int)(height * 0.85));
            int minOverlap = Math.Max(32, (width * height) / 10);
            double bestCost = double.MaxValue;
            double bestScore = double.MaxValue;
            int bestDx = 0, bestDy = 0;

            for (int dyMag = 0; dyMag <= maxDy; dyMag++)
            {
                for (int dySign = 1; dySign >= -1; dySign -= 2)
                {
                    int dy = dyMag * dySign;
                    if (dyMag == 0 && dySign == -1) continue;

                    for (int dxMag = 0; dxMag <= maxDx; dxMag++)
                    {
                        for (int dxSign = 1; dxSign >= -1; dxSign -= 2)
                        {
                            int dx = dxMag * dxSign;
                            if (dxMag == 0 && dxSign == -1) continue;

                            int xStart = Math.Max(0, -dx), yStart = Math.Max(0, -dy);
                            int xEnd = Math.Min(width, width - dx), yEnd = Math.Min(height, height - dy);
                            int overlapW = xEnd - xStart, overlapH = yEnd - yStart;
                            if (overlapW <= 0 || overlapH <= 0 || overlapW * overlapH < minOverlap) continue;

                            double score = AverageDiff(previous, current, dx, dy, xStart, yStart, xEnd, yEnd);
                            double penalty = 1.0 + 0.10 * ((double)dyMag / height) + 0.05 * ((double)dxMag / width);
                            double cost = score * penalty + (Math.Abs(dx) * 0.10);

                            if (cost < bestCost - 1e-4)
                            {
                                bestCost = cost;
                                bestScore = score;
                                bestDx = dx;
                                bestDy = dy;
                            }
                        }
                    }
                }
            }

            int originalDx = (int)Math.Round((double)bestDx * previous.Step);
            int originalDy = (int)Math.Round((double)bestDy * previous.Step);
            return new MovementEstimate(originalDx, originalDy, bestScore <= MaxAverageDiff);
        }

        private static double AverageDiff(SampleFrame previous, SampleFrame current, int dx, int dy, int xStart, int yStart, int xEnd, int yEnd)
        {
            long diff = 0; int count = 0;
            int stride = Math.Max(1, Math.Min(xEnd - xStart, yEnd - yStart) / 40);
            for (int y = yStart; y < yEnd; y += stride)
            {
                int previousRow = (y + dy) * previous.Width;
                int currentRow = y * current.Width;
                for (int x = xStart; x < xEnd; x += stride)
                {
                    diff += Math.Abs(previous.Gray[previousRow + x + dx] - current.Gray[currentRow + x]);
                    count++;
                }
            }
            return count == 0 ? double.MaxValue : (double)diff / count;
        }

        private byte[] BuildBand(Image<Bgra32> frame, Rectangle area)
        {
            _bandWidth = area.Width;
            _bandHeight = Math.Min(BandHeightPixels, area.Height);
            if (_bandWidth <= 0 || _bandHeight <= 0)
            {
                return Array.Empty<byte>();
            }
            byte[] band = new byte[_bandWidth * _bandHeight];
            int top = area.Top + (area.Height - _bandHeight) / 2;
            frame.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < _bandHeight; y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(Math.Min(frame.Height - 1, Math.Max(0, top + y)));
                    int target = y * _bandWidth;
                    for (int x = 0; x < _bandWidth; x++)
                    {
                        Bgra32 px = row[Math.Min(frame.Width - 1, Math.Max(0, area.Left + x))];
                        band[target + x] = (byte)((px.R * 30 + px.G * 59 + px.B * 11) / 100);
                    }
                }
            });
            return band;
        }

        private MovementEstimate RefineMovement(byte[] previous, byte[] current, MovementEstimate coarse)
        {
            if (previous == null || current == null || previous.Length != current.Length || _bandWidth <= 0 || _bandHeight <= 0)
                return coarse;

            int yRadius = Math.Min(24, Math.Max(8, _bandWidth / 160 + 4));
            int xRadius = 4;
            const int stride = 3;
            int bestDx = coarse.DeltaX, bestDy = coarse.DeltaY;
            double bestCost = double.MaxValue;
            double bestScore = double.MaxValue;

            for (int dyOffset = 0; dyOffset <= yRadius; dyOffset++)
            {
                for (int dySign = 1; dySign >= -1; dySign -= 2)
                {
                    int dy = coarse.DeltaY + dyOffset * dySign;
                    if (dyOffset == 0 && dySign == -1) continue;

                    for (int dxOffset = 0; dxOffset <= xRadius; dxOffset++)
                    {
                        for (int dxSign = 1; dxSign >= -1; dxSign -= 2)
                        {
                            int dx = coarse.DeltaX + dxOffset * dxSign;
                            if (dxOffset == 0 && dxSign == -1) continue;

                            double score = BandDiff(previous, current, dx, dy, stride);
                            double cost = score + (Math.Abs(dx) * 0.10) + (dyOffset * 0.02);
                            if (cost < bestCost - 1e-4)
                            {
                                bestCost = cost;
                                bestScore = score;
                                bestDx = dx;
                                bestDy = dy;
                            }
                        }
                    }
                }
            }

            if (bestScore <= MaxRefinedDiff)
            {
                return new MovementEstimate(bestDx, bestDy, true);
            }

            // If coarse estimate was already reliable across the entire frame, preserve it rather than discarding the frame
            if (coarse.IsReliable)
            {
                return coarse;
            }

            return new MovementEstimate(bestDx, bestDy, false);
        }

        private double BandDiff(byte[] previous, byte[] current, int dx, int dy, int stride)
        {
            int xStart = Math.Max(0, -dx), yStart = Math.Max(0, -dy);
            int xEnd = Math.Min(_bandWidth, _bandWidth - dx), yEnd = Math.Min(_bandHeight, _bandHeight - dy);
            if (xEnd - xStart < 8 || yEnd - yStart < 8) return double.MaxValue;

            long diff = 0; int count = 0;
            for (int y = yStart; y < yEnd; y += stride)
            {
                int previousRow = (y + dy) * _bandWidth;
                int currentRow = y * _bandWidth;
                for (int x = xStart; x < xEnd; x += stride)
                {
                    diff += Math.Abs(previous[previousRow + x + dx] - current[currentRow + x]);
                    count++;
                }
            }
            return count == 0 ? double.MaxValue : (double)diff / count;
        }
    }

    internal sealed class ScrollSegment
    {
        public ScrollSegment(Image<Bgra32> image, int x, int y) { Image = image; X = x; Y = y; }
        public Image<Bgra32> Image { get; }
        public int X { get; }
        public int Y { get; }
    }

    internal readonly struct MovementEstimate
    {
        public MovementEstimate(int deltaX, int deltaY, bool reliable) { DeltaX = deltaX; DeltaY = deltaY; IsReliable = reliable; }
        public int DeltaX { get; }
        public int DeltaY { get; }
        public bool IsReliable { get; }
        public static MovementEstimate Failed => new MovementEstimate(0, 0, false);
    }

    internal sealed class SampleFrame : IDisposable
    {
        private SampleFrame(byte[] gray, int width, int height, int step) { Gray = gray; Width = width; Height = height; Step = step; }
        public byte[] Gray { get; private set; }
        public int Width { get; }
        public int Height { get; }
        public int Step { get; }

        public static SampleFrame Create(Image<Bgra32> image, Rectangle area)
        {
            int contentWidth = area.Width > 0 ? area.Width : image.Width;
            int contentHeight = area.Height > 0 ? area.Height : image.Height;
            
            if (area.Width <= 0 || area.Height <= 0)
            {
                area = new Rectangle(0, 0, image.Width, image.Height);
                contentWidth = image.Width;
                contentHeight = image.Height;
            }

            int step = Math.Max(1, Math.Max(contentWidth / 180, contentHeight / 140));
            int sampleWidth = Math.Max(1, contentWidth / step);
            int sampleHeight = Math.Max(1, contentHeight / step);
            byte[] gray = new byte[sampleWidth * sampleHeight];

            image.ProcessPixelRows(accessor =>
            {
                for (int sy = 0; sy < sampleHeight; sy++)
                {
                    int sourceY = Math.Min(image.Height - 1, Math.Max(0, area.Top + sy * step));
                    Span<Bgra32> row = accessor.GetRowSpan(sourceY);
                    int targetRow = sy * sampleWidth;
                    for (int sx = 0; sx < sampleWidth; sx++)
                    {
                        int sourceX = Math.Min(image.Width - 1, Math.Max(0, area.Left + sx * step));
                        Bgra32 px = row[sourceX];
                        gray[targetRow + sx] = (byte)((px.R * 30 + px.G * 59 + px.B * 11) / 100);
                    }
                }
            });

            return new SampleFrame(gray, sampleWidth, sampleHeight, step);
        }

        public void Dispose() { Gray = null; }
    }
}
