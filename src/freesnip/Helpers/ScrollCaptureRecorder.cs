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

        /// <summary>
        /// Optional axis hint from the UI (e.g. mouse wheel direction).
        /// true = vertical, false = horizontal, null = unknown.
        /// </summary>
        public bool? AxisHint
        {
            get => _stitcher.AxisHint;
            set => _stitcher.AxisHint = value;
        }

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
        private const double MaxTileSad = 18.0;
        private const double StationarySadThreshold = 4.0;
        private const double BlankVarianceThreshold = 6.0;

        public const long DefaultMaxCompositePixels = 180L * 1024L * 1024L;
        public const int DefaultMaxSegments = int.MaxValue;

        public const long MaxCompositePixels = DefaultMaxCompositePixels;
        public const int MaxSegments = DefaultMaxSegments;

        public long MaxCompositePixelsLimit { get; set; } = DefaultMaxCompositePixels;
        public int MaxSegmentsLimit { get; set; } = DefaultMaxSegments;

        private readonly List<ScrollSegment> _segments = new List<ScrollSegment>();
        private readonly List<(Image<Bgra32> Frame, FrameLuma Luma, int OffsetX, int OffsetY)> _recentFrames = new();
        private const int MaxRecentFrames = 3;

        private FrameLuma _previousLuma;
        private Image<Bgra32> _previousFrame;
        private Image<Bgra32> _firstFrame;
        private Image<Bgra32> _lastFrame;

        private int _frameWidth;
        private int _frameHeight;
        private int _offsetX;
        private int _offsetY;
        private bool _viewportDetected;
        private Rectangle _viewport;
        private bool _isTrackingLost;
        private bool _scrollAxisLocked;
        private bool _isVerticalScroll = true;
        private int _consecutiveVerticalVotes;
        private int _consecutiveHorizontalVotes;
        private bool _disposed;

        /// <summary>
        /// Optional axis hint from the UI (e.g. mouse wheel direction).
        /// true = vertical hint, false = horizontal hint, null = no hint.
        /// This is advisory only; pixel overlap verification is authoritative.
        /// </summary>
        public bool? AxisHint { get; set; }

        public int AcceptedFrames { get; private set; }
        public int SegmentCount => _segments.Count;
        public bool IsTrackingLost => _isTrackingLost;
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
        public Rectangle Viewport => _viewport;
        public bool ViewportDetected => _viewportDetected;

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
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (IsSegmentCeilingReached)
                {
                    Log.Info("[STEP:WARN] ScrollFrameStitcher.AddFrame - Segment or memory ceiling reached.");
                    return ScrollFrameStatus.Rejected;
                }

                if (AcceptedFrames == 0)
                {
                    _frameWidth = frame.Width;
                    _frameHeight = frame.Height;
                    _firstFrame = frame.Clone(x => { });
                    _lastFrame = frame.Clone(x => { });
                    _previousFrame = frame.Clone(x => { });
                    _previousLuma = new FrameLuma(frame);
                    _recentFrames.Add((frame.Clone(x => { }), new FrameLuma(frame), 0, 0));

                    AcceptedFrames = 1;
                    Log.Info($"[STEP:SUCCESS] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Accepted initial anchor frame ({_frameWidth}x{_frameHeight}).");
                    return ScrollFrameStatus.Accepted;
                }

                if (frame.Width != _frameWidth || frame.Height != _frameHeight)
                {
                    Log.Warn($"[STEP:FAIL] ScrollFrameStitcher.AddFrame - Frame dimension mismatch: expected {_frameWidth}x{_frameHeight}, got {frame.Width}x{frame.Height}.");
                    return ScrollFrameStatus.Rejected;
                }

                var currentLuma = new FrameLuma(frame);

                if (!_viewportDetected)
                {
                    var motion = EstimateGlobalMotion(_previousLuma, currentLuma);
                    if (!motion.IsReliable)
                    {
                        currentLuma.Dispose();
                        Log.Info($"[STEP:WARN] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Initial motion estimation unreliable.");
                        return ScrollFrameStatus.Rejected;
                    }

                    if (Math.Abs(motion.DeltaX) < MinMovementPixels && Math.Abs(motion.DeltaY) < MinMovementPixels)
                    {
                        currentLuma.Dispose();
                        return ScrollFrameStatus.Duplicate;
                    }

                    // Use coarse motion for axis inference (not locked yet)
                    int coarseDx = motion.DeltaX;
                    int coarseDy = motion.DeltaY;

                    // Update axis consensus but do NOT immediately lock from a single frame
                    UpdateAxisConsensus(coarseDx, coarseDy);

                    int effDx = (_scrollAxisLocked && _isVerticalScroll) ? 0 : coarseDx;
                    int effDy = (_scrollAxisLocked && !_isVerticalScroll) ? 0 : coarseDy;

                    _viewport = DetectScrollingViewport(_previousLuma, currentLuma, effDx, effDy);
                    _viewportDetected = true;
                    Log.Info($"[STEP:SUCCESS] ScrollFrameStitcher.AddFrame - Viewport detected: [{_viewport.X}, {_viewport.Y}, {_viewport.Width}x{_viewport.Height}] (Motion: dx={effDx}, dy={effDy}).");

                    // Verify overlap displacement within _viewport
                    var verified = VerifyOverlapDisplacement(_previousLuma, currentLuma, effDx, effDy);
                    if (verified == null)
                    {
                        // Cannot verify overlap — accept viewport detection but reject strip
                        _segments.Clear();
                        _segments.Add(new ScrollSegment(_firstFrame.Clone(ctx => ctx.Crop(_viewport)), 0, 0));
                        UpdatePreviousFrame(frame, currentLuma, _offsetX, _offsetY);
                        AcceptedFrames++;
                        _isTrackingLost = false;
                        Log.Info($"[STEP:WARN] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Viewport detected but overlap verification failed. Strip skipped.");
                        return ScrollFrameStatus.Accepted;
                    }

                    int vDx = verified.Value.dx;
                    int vDy = verified.Value.dy;

                    _segments.Clear();
                    _segments.Add(new ScrollSegment(_firstFrame.Clone(ctx => ctx.Crop(_viewport)), 0, 0));

                    _offsetX += vDx;
                    _offsetY += vDy;
                    AddVisibleStrips(frame, _offsetX, _offsetY, vDx, vDy);

                    UpdatePreviousFrame(frame, currentLuma, _offsetX, _offsetY);
                    AcceptedFrames++;
                    _isTrackingLost = false;
                    return ScrollFrameStatus.Accepted;
                }

                bool? lockAxis = _scrollAxisLocked ? _isVerticalScroll : null;
                var vpMotion = EstimateViewportMotion(_previousLuma, currentLuma, _viewport, lockAxis);
                if (vpMotion.IsReliable)
                {
                    int effDx = _scrollAxisLocked && _isVerticalScroll ? 0 : vpMotion.DeltaX;
                    int effDy = _scrollAxisLocked && !_isVerticalScroll ? 0 : vpMotion.DeltaY;

                    if (Math.Abs(effDx) < MinMovementPixels && Math.Abs(effDy) < MinMovementPixels)
                    {
                        currentLuma.Dispose();
                        return ScrollFrameStatus.Duplicate;
                    }

                    // Verify overlap before committing
                    var verified = VerifyOverlapDisplacement(_previousLuma, currentLuma, effDx, effDy);
                    if (verified == null)
                    {
                        // Overlap verification failed — fall through to re-acquisition
                        Log.Info($"[STEP:WARN] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Viewport motion overlap verification failed (dx={effDx}, dy={effDy}). Trying fallback.");
                    }
                    else
                    {
                        int vDx = verified.Value.dx;
                        int vDy = verified.Value.dy;
                        UpdateAxisConsensus(vDx, vDy);

                        _offsetX += vDx;
                        _offsetY += vDy;
                        AddVisibleStrips(frame, _offsetX, _offsetY, vDx, vDy);

                        UpdatePreviousFrame(frame, currentLuma, _offsetX, _offsetY);
                        AcceptedFrames++;
                        _isTrackingLost = false;
                        return ScrollFrameStatus.Accepted;
                    }
                }

                var fallbackGlobal = EstimateGlobalMotion(_previousLuma, currentLuma, lockAxis);
                if (fallbackGlobal.IsReliable)
                {
                    int effDx = _scrollAxisLocked && _isVerticalScroll ? 0 : fallbackGlobal.DeltaX;
                    int effDy = _scrollAxisLocked && !_isVerticalScroll ? 0 : fallbackGlobal.DeltaY;

                    if (Math.Abs(effDx) < MinMovementPixels && Math.Abs(effDy) < MinMovementPixels)
                    {
                        currentLuma.Dispose();
                        return ScrollFrameStatus.Duplicate;
                    }

                    // Verify overlap before committing
                    var verified = VerifyOverlapDisplacement(_previousLuma, currentLuma, effDx, effDy);
                    if (verified == null)
                    {
                        Log.Info($"[STEP:WARN] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Global fallback overlap verification failed (dx={effDx}, dy={effDy}). Trying re-acquisition.");
                    }
                    else
                    {
                        int vDx = verified.Value.dx;
                        int vDy = verified.Value.dy;
                        UpdateAxisConsensus(vDx, vDy);

                        _offsetX += vDx;
                        _offsetY += vDy;
                        AddVisibleStrips(frame, _offsetX, _offsetY, vDx, vDy);

                        UpdatePreviousFrame(frame, currentLuma, _offsetX, _offsetY);
                        AcceptedFrames++;
                        _isTrackingLost = false;
                        Log.Info($"[STEP:SUCCESS] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Global motion fallback accepted (verified dx={vDx}, dy={vDy}).");
                        return ScrollFrameStatus.Accepted;
                    }
                }

                Log.Info($"[STEP:WARN] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Viewport tracking lost against previous frame. Attempting re-acquisition against {_recentFrames.Count} recent frames.");
                for (int i = _recentFrames.Count - 1; i >= 0; i--)
                {
                    var recent = _recentFrames[i];
                    var reacquireMotion = EstimateViewportMotion(recent.Luma, currentLuma, _viewport, lockAxis);
                    if (!reacquireMotion.IsReliable)
                    {
                        reacquireMotion = EstimateGlobalMotion(recent.Luma, currentLuma, lockAxis);
                    }

                    int effDx = _scrollAxisLocked && _isVerticalScroll ? 0 : reacquireMotion.DeltaX;
                    int effDy = _scrollAxisLocked && !_isVerticalScroll ? 0 : reacquireMotion.DeltaY;

                    if (reacquireMotion.IsReliable && (Math.Abs(effDx) >= MinMovementPixels || Math.Abs(effDy) >= MinMovementPixels))
                    {
                        // Verify overlap against this recent frame
                        var verified = VerifyOverlapDisplacement(recent.Luma, currentLuma, effDx, effDy);
                        if (verified == null) continue; // try next recent frame

                        int vDx = verified.Value.dx;
                        int vDy = verified.Value.dy;

                        int newOffsetX = recent.OffsetX + vDx;
                        int newOffsetY = recent.OffsetY + vDy;
                        int stepDx = newOffsetX - _offsetX;
                        int stepDy = newOffsetY - _offsetY;

                        _offsetX = newOffsetX;
                        _offsetY = newOffsetY;
                        AddVisibleStrips(frame, _offsetX, _offsetY, stepDx, stepDy);

                        UpdatePreviousFrame(frame, currentLuma, _offsetX, _offsetY);
                        AcceptedFrames++;
                        _isTrackingLost = false;
                        Log.Info($"[STEP:SUCCESS] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Re-acquired tracking against recent frame {i} (verified offset: {_offsetX}, {_offsetY}).");
                        return ScrollFrameStatus.Accepted;
                    }
                }

                _isTrackingLost = true;
                currentLuma.Dispose();
                Log.Warn($"[STEP:FAIL] ScrollFrameStitcher.AddFrame ({sw.ElapsedMilliseconds}ms) - Re-acquisition failed. Rejecting frame to prevent corruption.");
                return ScrollFrameStatus.Rejected;
            }
            finally
            {
                frame.Dispose();
            }
        }

        private static MovementEstimate EstimateGlobalMotion(FrameLuma prev, FrameLuma curr, bool? lockVertical = null)
        {
            int w = prev.Width;
            int h = prev.Height;
            int blockSize = Math.Clamp(Math.Min(w, h) / 20, 16, 32);
            int cols = w / blockSize;
            int rows = h / blockSize;

            var candidateBlocks = new List<(int X0, int Y0, int X1, int Y1)>();
            for (int r = 0; r < rows; r++)
            {
                int y0 = r * blockSize;
                int y1 = Math.Min(h, (r + 1) * blockSize);
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * blockSize;
                    int x1 = Math.Min(w, (c + 1) * blockSize);

                    double variance = ComputeVariance(prev, x0, y0, x1, y1);
                    if (variance < BlankVarianceThreshold) continue;

                    double sad0 = ComputeBlockSad(prev, curr, x0, y0, x1, y1, 0, 0);
                    if (sad0 > StationarySadThreshold)
                    {
                        candidateBlocks.Add((x0, y0, x1, y1));
                    }
                }
            }

            if (candidateBlocks.Count == 0)
            {
                return new MovementEstimate(0, 0, true);
            }

            int sampleCount = Math.Min(candidateBlocks.Count, 32);
            var sampledBlocks = new List<(int X0, int Y0, int X1, int Y1)>(sampleCount);
            int step = Math.Max(1, candidateBlocks.Count / sampleCount);
            for (int i = 0; i < candidateBlocks.Count && sampledBlocks.Count < sampleCount; i += step)
            {
                sampledBlocks.Add(candidateBlocks[i]);
            }

            int maxDy = Math.Max(2, (int)(h * 0.85));
            int maxDx = Math.Max(2, (int)(w * 0.85));

            int bestDx = 0, bestDy = 0;
            int bestAgreement = 0;
            double bestAvgSad = double.MaxValue;
            double bestScore = double.MaxValue;

            void EvaluateCandidate(int dx, int dy)
            {
                int agreeing = 0;
                double totalSad = 0;
                foreach (var b in sampledBlocks)
                {
                    int bx0 = b.X0 + dx;
                    int by0 = b.Y0 + dy;
                    int bx1 = b.X1 + dx;
                    int by1 = b.Y1 + dy;
                    if (bx0 < 0 || by0 < 0 || bx1 > w || by1 > h) continue;

                    double sad = ComputeBlockSad(prev, curr, b.X0, b.Y0, b.X1, b.Y1, dx, dy);
                    if (sad <= MaxTileSad)
                    {
                        agreeing++;
                        totalSad += sad;
                    }
                }

                if (agreeing == 0) return;
                double avgSad = totalSad / agreeing;
                double ratio = (double)agreeing / sampledBlocks.Count;
                if (ratio < 0.20 && agreeing < 2) return;

                double score = (1.0 - ratio) * 100.0 + avgSad + (Math.Abs(dx) + Math.Abs(dy)) * 0.01;
                if (score < bestScore - 1e-4)
                {
                    bestScore = score;
                    bestAgreement = agreeing;
                    bestAvgSad = avgSad;
                    bestDx = dx;
                    bestDy = dy;
                }
            }

            int coarseStepY = h > 400 ? 2 : 1;
            if (lockVertical != false)
            {
                for (int dyMag = 2; dyMag <= maxDy; dyMag += coarseStepY)
                {
                    EvaluateCandidate(0, dyMag);
                    EvaluateCandidate(0, -dyMag);
                }
            }

            int coarseStepX = w > 400 ? 2 : 1;
            if (lockVertical != true)
            {
                for (int dxMag = 2; dxMag <= maxDx; dxMag += coarseStepX)
                {
                    EvaluateCandidate(dxMag, 0);
                    EvaluateCandidate(-dxMag, 0);
                }
            }

            if (bestAgreement > 0)
            {
                int cDx = bestDx;
                int cDy = bestDy;
                int dyStart = lockVertical == false ? 0 : cDy - coarseStepY;
                int dyEnd = lockVertical == false ? 0 : cDy + coarseStepY;
                int dxStart = lockVertical == true ? 0 : cDx - 2;
                int dxEnd = lockVertical == true ? 0 : cDx + 2;

                for (int dy = dyStart; dy <= dyEnd; dy++)
                {
                    for (int dx = dxStart; dx <= dxEnd; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        EvaluateCandidate(dx, dy);
                    }
                }
            }

            bool reliable = bestAgreement >= 2 && bestAvgSad <= MaxTileSad;
            return new MovementEstimate(bestDx, bestDy, reliable);
        }

        private static Rectangle DetectScrollingViewport(FrameLuma prev, FrameLuma curr, int dx, int dy)
        {
            int w = prev.Width;
            int h = prev.Height;

            int xInteriorStart = (int)(w * 0.15);
            int xInteriorEnd = (int)(w * 0.85);
            int yInteriorStart = (int)(h * 0.15);
            int yInteriorEnd = (int)(h * 0.85);

            int maxHeaderH = Math.Min(220, (int)(h * 0.30));
            int exactTop = 0;
            int firstMovingY = -1;
            int lastStationaryFeatureY = -1;

            for (int y = 0; y < maxHeaderH; y += 2)
            {
                double diff0 = RowAverageDiff(prev, curr, y, xInteriorStart, xInteriorEnd, 0, 0);
                double diffMotion = RowAverageDiff(prev, curr, y, xInteriorStart, xInteriorEnd, dx, dy);

                bool isRowMoving = diff0 > StationarySadThreshold && diffMotion <= MaxTileSad && diffMotion < diff0 - 1.5;
                if (isRowMoving && firstMovingY < 0)
                {
                    firstMovingY = y;
                    break;
                }

                if (diff0 <= StationarySadThreshold)
                {
                    lastStationaryFeatureY = y;
                }
            }

            if (firstMovingY >= 0)
            {
                exactTop = (lastStationaryFeatureY >= 0 && lastStationaryFeatureY <= firstMovingY)
                    ? Math.Min(firstMovingY, lastStationaryFeatureY + 2)
                    : firstMovingY;
            }
            else
            {
                exactTop = 0;
            }

            int maxFooterH = Math.Min(120, (int)(h * 0.20));
            int exactBottom = h;
            int lastMovingY = -1;
            int firstStationaryFooterY = -1;

            for (int y = h - 1; y >= h - maxFooterH; y -= 2)
            {
                double diff0 = RowAverageDiff(prev, curr, y, xInteriorStart, xInteriorEnd, 0, 0);
                double diffMotion = RowAverageDiff(prev, curr, y, xInteriorStart, xInteriorEnd, dx, dy);

                bool isRowMoving = diff0 > StationarySadThreshold && diffMotion <= MaxTileSad && diffMotion < diff0 - 1.5;
                if (isRowMoving && lastMovingY < 0)
                {
                    lastMovingY = y;
                    break;
                }

                if (diff0 <= StationarySadThreshold)
                {
                    firstStationaryFooterY = y;
                }
            }

            if (lastMovingY >= 0)
            {
                exactBottom = (firstStationaryFooterY >= 0 && firstStationaryFooterY >= lastMovingY)
                    ? Math.Max(lastMovingY + 1, firstStationaryFooterY)
                    : lastMovingY + 1;
            }
            else
            {
                exactBottom = h;
            }

            int maxLeftW = Math.Min(500, (int)(w * 0.45));
            int exactLeft = 0;
            int firstMovingX = -1;
            int lastStationaryFeatureX = -1;

            for (int x = 0; x < maxLeftW; x += 2)
            {
                double diff0 = ColAverageDiff(prev, curr, x, yInteriorStart, yInteriorEnd, 0, 0);
                double diffMotion = ColAverageDiff(prev, curr, x, yInteriorStart, yInteriorEnd, dx, dy);

                bool isColMoving = diff0 > StationarySadThreshold && diffMotion <= MaxTileSad && diffMotion < diff0 - 1.5;
                if (isColMoving && firstMovingX < 0)
                {
                    firstMovingX = x;
                    break;
                }

                if (diff0 <= StationarySadThreshold)
                {
                    lastStationaryFeatureX = x;
                }
            }

            if (firstMovingX >= 0)
            {
                exactLeft = (lastStationaryFeatureX >= 0 && lastStationaryFeatureX <= firstMovingX)
                    ? Math.Min(firstMovingX, lastStationaryFeatureX + 2)
                    : firstMovingX;
            }
            else
            {
                exactLeft = 0;
            }

            int maxRightW = Math.Min(120, (int)(w * 0.15));
            int exactRight = w;
            int lastMovingX = -1;
            int firstStationaryScrollbarX = -1;

            for (int x = w - 1; x >= w - maxRightW; x -= 2)
            {
                double diff0 = ColAverageDiff(prev, curr, x, yInteriorStart, yInteriorEnd, 0, 0);
                double diffMotion = ColAverageDiff(prev, curr, x, yInteriorStart, yInteriorEnd, dx, dy);

                bool isColMoving = diff0 > StationarySadThreshold && diffMotion <= MaxTileSad && diffMotion < diff0 - 1.5;
                if (isColMoving && lastMovingX < 0)
                {
                    lastMovingX = x;
                    break;
                }

                if (diff0 <= StationarySadThreshold)
                {
                    firstStationaryScrollbarX = x;
                }
            }

            if (lastMovingX >= 0)
            {
                exactRight = (firstStationaryScrollbarX >= 0 && firstStationaryScrollbarX >= lastMovingX)
                    ? Math.Max(lastMovingX + 1, firstStationaryScrollbarX)
                    : lastMovingX + 1;
            }
            else
            {
                exactRight = w;
            }

            int vpWidth = exactRight - exactLeft;
            int vpHeight = exactBottom - exactTop;

            if (vpWidth < (int)(w * 0.30))
            {
                exactLeft = 0;
                exactRight = w;
                vpWidth = w;
            }

            if (vpHeight < (int)(h * 0.30))
            {
                exactTop = 0;
                exactBottom = h;
                vpHeight = h;
            }

            return new Rectangle(exactLeft, exactTop, vpWidth, vpHeight);
        }

        private static MovementEstimate EstimateViewportMotion(FrameLuma prev, FrameLuma curr, Rectangle vp, bool? lockVertical = null)
        {
            int vx0 = vp.X;
            int vy0 = vp.Y;
            int vx1 = vp.Right;
            int vy1 = vp.Bottom;
            int vw = vp.Width;
            int vh = vp.Height;

            int blockSize = Math.Clamp(Math.Min(vw, vh) / 20, 16, 32);
            int cols = vw / blockSize;
            int rows = vh / blockSize;

            var candidateBlocks = new List<(int X0, int Y0, int X1, int Y1)>();
            for (int r = 0; r < rows; r++)
            {
                int y0 = vy0 + r * blockSize;
                int y1 = Math.Min(vy1, vy0 + (r + 1) * blockSize);
                for (int c = 0; c < cols; c++)
                {
                    int x0 = vx0 + c * blockSize;
                    int x1 = Math.Min(vx1, vx0 + (c + 1) * blockSize);

                    double variance = ComputeVariance(prev, x0, y0, x1, y1);
                    if (variance < BlankVarianceThreshold) continue;

                    double sad0 = ComputeBlockSad(prev, curr, x0, y0, x1, y1, 0, 0);
                    if (sad0 > StationarySadThreshold)
                    {
                        candidateBlocks.Add((x0, y0, x1, y1));
                    }
                }
            }

            if (candidateBlocks.Count >= 2)
            {
                int sampleCount = Math.Min(candidateBlocks.Count, 32);
                var sampledBlocks = new List<(int X0, int Y0, int X1, int Y1)>(sampleCount);
                int step = Math.Max(1, candidateBlocks.Count / sampleCount);
                for (int i = 0; i < candidateBlocks.Count && sampledBlocks.Count < sampleCount; i += step)
                {
                    sampledBlocks.Add(candidateBlocks[i]);
                }

                int maxDy = Math.Max(2, (int)(vh * 0.85));
                int maxDx = Math.Max(2, (int)(vw * 0.85));

                int bestDx = 0, bestDy = 0;
                int bestAgreement = 0;
                double bestAvgSad = double.MaxValue;
                double bestScore = double.MaxValue;

                void EvaluateCandidate(int dx, int dy)
                {
                    int agreeing = 0;
                    double totalSad = 0;
                    foreach (var b in sampledBlocks)
                    {
                        int bx0 = b.X0 + dx;
                        int by0 = b.Y0 + dy;
                        int bx1 = b.X1 + dx;
                        int by1 = b.Y1 + dy;
                        if (bx0 < 0 || by0 < 0 || bx1 > prev.Width || by1 > prev.Height) continue;

                        double sad = ComputeBlockSad(prev, curr, b.X0, b.Y0, b.X1, b.Y1, dx, dy);
                        if (sad <= MaxTileSad)
                        {
                            agreeing++;
                            totalSad += sad;
                        }
                    }

                    if (agreeing == 0) return;
                    double avgSad = totalSad / agreeing;
                    double ratio = (double)agreeing / sampledBlocks.Count;
                    if (ratio < 0.20 && agreeing < 2) return;

                    double penalty = (Math.Abs(dx) + Math.Abs(dy)) * 0.0005;
                    double score = avgSad - (agreeing * 0.5) + penalty;
                    if (score < bestScore - 1e-4)
                    {
                        bestScore = score;
                        bestAvgSad = avgSad;
                        bestAgreement = agreeing;
                        bestDx = dx;
                        bestDy = dy;
                    }
                }

                int coarseStepY = vh > 400 ? 2 : 1;
                if (lockVertical != false)
                {
                    for (int dyMag = 2; dyMag <= maxDy; dyMag += coarseStepY)
                    {
                        EvaluateCandidate(0, dyMag);
                        EvaluateCandidate(0, -dyMag);
                    }
                }

                int coarseStepX = vw > 400 ? 2 : 1;
                if (lockVertical != true)
                {
                    for (int dxMag = 2; dxMag <= maxDx; dxMag += coarseStepX)
                    {
                        EvaluateCandidate(dxMag, 0);
                        EvaluateCandidate(-dxMag, 0);
                    }
                }

                if (bestAgreement > 0)
                {
                    int cDx = bestDx;
                    int cDy = bestDy;
                    int dyStart = lockVertical == false ? 0 : cDy - coarseStepY;
                    int dyEnd = lockVertical == false ? 0 : cDy + coarseStepY;
                    int dxStart = lockVertical == true ? 0 : cDx - 2;
                    int dxEnd = lockVertical == true ? 0 : cDx + 2;

                    for (int dy = dyStart; dy <= dyEnd; dy++)
                    {
                        for (int dx = dxStart; dx <= dxEnd; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            EvaluateCandidate(dx, dy);
                        }
                    }
                }

                if (bestAgreement >= 2 && bestAvgSad <= MaxTileSad)
                {
                    return new MovementEstimate(bestDx, bestDy, true);
                }
            }

            int areaMaxDy = Math.Max(2, (int)(vh * 0.85));
            int areaMaxDx = Math.Max(2, (int)(vw * 0.85));
            int areaBestDx = 0, areaBestDy = 0;
            double areaBestSad = double.MaxValue;

            void CheckShift(int dx, int dy)
            {
                int ox0 = Math.Max(vx0, vx0 - dx);
                int oy0 = Math.Max(vy0, vy0 - dy);
                int ox1 = Math.Min(vx1, vx1 - dx);
                int oy1 = Math.Min(vy1, vy1 - dy);
                int ow = ox1 - ox0;
                int oh = oy1 - oy0;
                if (ow < 16 || oh < 16 || (long)ow * oh < ((long)vw * vh) / 10) return;

                double sad = ComputeAreaSad(prev, curr, ox0, oy0, ox1, oy1, dx, dy);
                double penalty = (Math.Abs(dx) + Math.Abs(dy)) * 0.0005;
                double cost = sad + penalty;
                if (cost < areaBestSad - 1e-4)
                {
                    areaBestSad = cost;
                    areaBestDx = dx;
                    areaBestDy = dy;
                }
            }

            int aCoarseY = vh > 400 ? 2 : 1;
            if (lockVertical != false)
            {
                for (int dyMag = 0; dyMag <= areaMaxDy; dyMag += aCoarseY)
                {
                    CheckShift(0, dyMag);
                    if (dyMag > 0) CheckShift(0, -dyMag);
                }
            }

            int aCoarseX = vw > 400 ? 2 : 1;
            if (lockVertical != true)
            {
                for (int dxMag = aCoarseX; dxMag <= areaMaxDx; dxMag += aCoarseX)
                {
                    CheckShift(dxMag, 0);
                    CheckShift(-dxMag, 0);
                }
            }

            int acDx = areaBestDx;
            int acDy = areaBestDy;
            int aDyStart = lockVertical == false ? 0 : acDy - aCoarseY;
            int aDyEnd = lockVertical == false ? 0 : acDy + aCoarseY;
            int aDxStart = lockVertical == true ? 0 : acDx - 2;
            int aDxEnd = lockVertical == true ? 0 : acDx + 2;

            for (int dy = aDyStart; dy <= aDyEnd; dy++)
            {
                for (int dx = aDxStart; dx <= aDxEnd; dx++)
                {
                    CheckShift(dx, dy);
                }
            }

            bool areaReliable = areaBestSad <= MaxTileSad;
            return new MovementEstimate(areaBestDx, areaBestDy, areaReliable);
        }

        /// <summary>
        /// Second-stage overlap verifier. Searches ±SearchRadius px around the coarse
        /// displacement, scoring the overlapping viewport region using the same normalized
        /// SAD metric the motion estimator uses. Returns the verified displacement or null
        /// if no confident match is found (SAD > MaxTileSad for all candidates).
        /// </summary>
        internal const int OverlapSearchRadius = 3;
        internal const double OverlapConfidenceThreshold = MaxTileSad;

        private (int dx, int dy)? VerifyOverlapDisplacement(FrameLuma prevLuma, FrameLuma currLuma, int coarseDx, int coarseDy)
        {
            if (!_viewportDetected) return (coarseDx, coarseDy);

            int vx0 = _viewport.X;
            int vy0 = _viewport.Y;
            int vx1 = _viewport.Right;
            int vy1 = _viewport.Bottom;
            int vw = _viewport.Width;
            int vh = _viewport.Height;

            double bestSad = double.MaxValue;
            int bestDx = coarseDx, bestDy = coarseDy;

            // Determine search axes based on locked scroll direction
            bool searchVertical = !_scrollAxisLocked || _isVerticalScroll;
            bool searchHorizontal = !_scrollAxisLocked || !_isVerticalScroll;

            int dyLo = searchVertical ? coarseDy - OverlapSearchRadius : coarseDy;
            int dyHi = searchVertical ? coarseDy + OverlapSearchRadius : coarseDy;
            int dxLo = searchHorizontal ? coarseDx - OverlapSearchRadius : coarseDx;
            int dxHi = searchHorizontal ? coarseDx + OverlapSearchRadius : coarseDx;

            for (int dy = dyLo; dy <= dyHi; dy++)
            {
                for (int dx = dxLo; dx <= dxHi; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    // Compute the overlapping viewport region at this displacement
                    int ox0 = Math.Max(vx0, vx0 - dx);
                    int oy0 = Math.Max(vy0, vy0 - dy);
                    int ox1 = Math.Min(vx1, vx1 - dx);
                    int oy1 = Math.Min(vy1, vy1 - dy);
                    int ow = ox1 - ox0;
                    int oh = oy1 - oy0;

                    // Need a meaningful overlap area (at least 10% of viewport)
                    if (ow < 16 || oh < 16 || (long)ow * oh < ((long)vw * vh) / 10) continue;

                    double sad = ComputeAreaSad(prevLuma, currLuma, ox0, oy0, ox1, oy1, dx, dy);
                    // Slight bias toward the coarse estimate to break ties
                    double distPenalty = (Math.Abs(dx - coarseDx) + Math.Abs(dy - coarseDy)) * 0.05;
                    double cost = sad + distPenalty;

                    if (cost < bestSad - 1e-6)
                    {
                        bestSad = cost;
                        bestDx = dx;
                        bestDy = dy;
                    }
                }
            }

            // Reject if even the best candidate exceeds the confidence threshold
            if (bestSad > OverlapConfidenceThreshold)
            {
                return null;
            }

            return (bestDx, bestDy);
        }

        /// <summary>
        /// Updates axis consensus tracking based on a verified displacement.
        /// Requires 2 consecutive frames agreeing on the same dominant axis
        /// before locking. AxisHint from the UI counts as a pre-vote.
        /// </summary>
        private void UpdateAxisConsensus(int verifiedDx, int verifiedDy)
        {
            if (_scrollAxisLocked) return;

            bool frameIsVertical = Math.Abs(verifiedDy) >= Math.Abs(verifiedDx);

            if (frameIsVertical)
            {
                _consecutiveVerticalVotes++;
                _consecutiveHorizontalVotes = 0;
            }
            else
            {
                _consecutiveHorizontalVotes++;
                _consecutiveVerticalVotes = 0;
            }

            // AxisHint from UI counts as a pre-existing vote
            int verticalVotesNeeded = (AxisHint == true) ? 1 : 2;
            int horizontalVotesNeeded = (AxisHint == false) ? 1 : 2;

            // Horizontal lock requires strong dominance to prevent false axis classification
            bool horizontalConfident = _consecutiveHorizontalVotes >= horizontalVotesNeeded
                && Math.Abs(verifiedDx) >= 20
                && Math.Abs(verifiedDx) > (3 * Math.Abs(verifiedDy));

            if (_consecutiveVerticalVotes >= verticalVotesNeeded)
            {
                _isVerticalScroll = true;
                _scrollAxisLocked = true;
                Log.Info($"[STEP:INFO] Axis locked to VERTICAL after {_consecutiveVerticalVotes} consecutive votes (hint={AxisHint}).");
            }
            else if (horizontalConfident)
            {
                _isVerticalScroll = false;
                _scrollAxisLocked = true;
                Log.Info($"[STEP:INFO] Axis locked to HORIZONTAL after {_consecutiveHorizontalVotes} consecutive votes (hint={AxisHint}).");
            }
        }

        private void AddVisibleStrips(Image<Bgra32> frame, int offsetX, int offsetY, int deltaX, int deltaY)
        {
            if (_scrollAxisLocked && _isVerticalScroll)
            {
                deltaX = 0;
                offsetX = 0;
            }
            else if (_scrollAxisLocked && !_isVerticalScroll)
            {
                deltaY = 0;
                offsetY = 0;
            }

            int absY = Math.Abs(deltaY);
            int absX = Math.Abs(deltaX);

            if (absY >= absX && absY >= MinMovementPixels)
            {
                int h = Math.Min(absY, _viewport.Height);

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
                _segments.Add(new ScrollSegment(frame.Clone(ctx => ctx.Crop(crop)), 0, y));
            }
            else if (absX > absY && absX >= MinMovementPixels)
            {
                int w = Math.Min(absX, _viewport.Width);

                if (_frameHeight > 0 && MaxCompositePixelsLimit > 0)
                {
                    long maxAllowedWidth = MaxCompositePixelsLimit / _frameHeight;
                    long previousWidth = (long)_frameWidth + Math.Max(Math.Abs(offsetX - deltaX), Math.Abs(offsetY - deltaY));
                    if (previousWidth >= maxAllowedWidth)
                    {
                        return;
                    }
                    long remainingWidth = maxAllowedWidth - previousWidth;
                    if (w > remainingWidth)
                    {
                        w = (int)remainingWidth;
                    }
                }

                if (w <= 0) return;

                Rectangle crop = deltaX > 0
                    ? new Rectangle(_viewport.Right - w, _viewport.Top, w, _viewport.Height)
                    : new Rectangle(_viewport.X, _viewport.Top, w, _viewport.Height);
                int x = deltaX > 0 ? offsetX + _viewport.Width - w : offsetX;
                _segments.Add(new ScrollSegment(frame.Clone(ctx => ctx.Crop(crop)), x, 0));
            }
        }

        private void UpdatePreviousFrame(Image<Bgra32> frame, FrameLuma luma, int offsetX, int offsetY)
        {
            _previousLuma?.Dispose();
            _previousFrame?.Dispose();
            _previousLuma = luma;
            _previousFrame = frame.Clone(x => { });

            _lastFrame?.Dispose();
            _lastFrame = frame.Clone(x => { });

            _recentFrames.Add((frame.Clone(x => { }), new FrameLuma(frame), offsetX, offsetY));
            if (_recentFrames.Count > MaxRecentFrames)
            {
                var oldest = _recentFrames[0];
                _recentFrames.RemoveAt(0);
                oldest.Frame?.Dispose();
                oldest.Luma?.Dispose();
            }
        }

        private static double ComputeVariance(FrameLuma luma, int x0, int y0, int x1, int y1)
        {
            double sum = 0, sumSq = 0;
            int count = 0;
            int w = luma.Width;
            for (int y = y0; y < y1; y += 2)
            {
                int row = y * w;
                for (int x = x0; x < x1; x += 2)
                {
                    byte val = luma.Pixels[row + x];
                    sum += val;
                    sumSq += val * val;
                    count++;
                }
            }
            if (count == 0) return 0;
            double mean = sum / count;
            return Math.Max(0, (sumSq / count) - (mean * mean));
        }

        private static double ComputeBlockSad(FrameLuma prev, FrameLuma curr, int x0, int y0, int x1, int y1, int dx, int dy)
        {
            long diff = 0;
            int count = 0;
            int w = prev.Width;
            int h = prev.Height;

            for (int y = y0; y < y1; y += 2)
            {
                int py = y + dy;
                if (py < 0 || py >= h) continue;
                int pRow = py * w;
                int cRow = y * w;
                for (int x = x0; x < x1; x += 2)
                {
                    int px = x + dx;
                    if (px < 0 || px >= w) continue;
                    diff += Math.Abs(prev.Pixels[pRow + px] - curr.Pixels[cRow + x]);
                    count++;
                }
            }
            return count == 0 ? double.MaxValue : (double)diff / count;
        }

        private static double ComputeAreaSad(FrameLuma prev, FrameLuma curr, int x0, int y0, int x1, int y1, int dx, int dy)
        {
            long diff = 0;
            int count = 0;
            int w = prev.Width;
            int h = prev.Height;
            int step = Math.Max(2, Math.Min(x1 - x0, y1 - y0) / 40);

            for (int y = y0; y < y1; y += step)
            {
                int py = y + dy;
                if (py < 0 || py >= h) continue;
                int pRow = py * w;
                int cRow = y * w;
                for (int x = x0; x < x1; x += step)
                {
                    int px = x + dx;
                    if (px < 0 || px >= w) continue;
                    diff += Math.Abs(prev.Pixels[pRow + px] - curr.Pixels[cRow + x]);
                    count++;
                }
            }
            return count == 0 ? double.MaxValue : (double)diff / count;
        }

        private static double RowAverageDiff(FrameLuma prev, FrameLuma curr, int y, int xStart, int xEnd, int dx, int dy)
        {
            int py = y + dy;
            if (y < 0 || y >= curr.Height || py < 0 || py >= prev.Height) return double.MaxValue;
            long diff = 0;
            int count = 0;
            int w = prev.Width;
            int step = Math.Max(1, (xEnd - xStart) / 60);
            int pRow = py * w;
            int cRow = y * w;

            for (int x = xStart; x < xEnd; x += step)
            {
                int px = x + dx;
                if (px < 0 || px >= w) continue;
                diff += Math.Abs(prev.Pixels[pRow + px] - curr.Pixels[cRow + x]);
                count++;
            }
            return count == 0 ? 0 : (double)diff / count;
        }

        private static double ColAverageDiff(FrameLuma prev, FrameLuma curr, int x, int yStart, int yEnd, int dx, int dy)
        {
            int px = x + dx;
            if (x < 0 || x >= curr.Width || px < 0 || px >= prev.Width) return double.MaxValue;
            long diff = 0;
            int count = 0;
            int w = prev.Width;
            int h = prev.Height;
            int step = Math.Max(1, (yEnd - yStart) / 60);

            for (int y = yStart; y < yEnd; y += step)
            {
                int py = y + dy;
                if (py < 0 || py >= h) continue;
                diff += Math.Abs(prev.Pixels[py * w + px] - curr.Pixels[y * w + x]);
                count++;
            }
            return count == 0 ? 0 : (double)diff / count;
        }

        private static double ComputeRowVariance(FrameLuma luma, int y, int xStart, int xEnd)
        {
            if (y < 0 || y >= luma.Height) return 0;
            double sum = 0, sumSq = 0;
            int count = 0;
            int w = luma.Width;
            int row = y * w;
            int step = Math.Max(1, (xEnd - xStart) / 60);
            for (int x = xStart; x < xEnd; x += step)
            {
                byte val = luma.Pixels[row + x];
                sum += val;
                sumSq += val * val;
                count++;
            }
            if (count == 0) return 0;
            double mean = sum / count;
            return Math.Max(0, (sumSq / count) - (mean * mean));
        }

        private static double ComputeColVariance(FrameLuma luma, int x, int yStart, int yEnd)
        {
            if (x < 0 || x >= luma.Width) return 0;
            double sum = 0, sumSq = 0;
            int count = 0;
            int w = luma.Width;
            int step = Math.Max(1, (yEnd - yStart) / 60);
            for (int y = yStart; y < yEnd; y += step)
            {
                byte val = luma.Pixels[y * w + x];
                sum += val;
                sumSq += val * val;
                count++;
            }
            if (count == 0) return 0;
            double mean = sum / count;
            return Math.Max(0, (sumSq / count) - (mean * mean));
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

            int headerH = _viewport.Top;
            int footerH = Math.Max(0, _frameHeight - _viewport.Bottom);
            int leftBarW = _viewport.Left;
            int rightBarW = Math.Max(0, _frameWidth - _viewport.Right);

            bool isVertical = _scrollAxisLocked ? _isVerticalScroll : (vpH >= vpW);
            int width = isVertical ? _frameWidth : (leftBarW + vpW + rightBarW);
            int height = isVertical ? (headerH + vpH + footerH) : _frameHeight;

            if (width <= 0 || height <= 0) return null;

            long totalPixels = (long)width * height;
            long pixelCeiling = MaxCompositePixelsLimit;
            if (totalPixels > pixelCeiling)
            {
                if (isVertical)
                {
                    int clampedHeight = (int)(pixelCeiling / width);
                    if (clampedHeight < _frameHeight) clampedHeight = _frameHeight;
                    Log.Warn($"Scroll capture composite dimension ({width}x{height}, {totalPixels} pixels) exceeds MaxCompositePixels ({pixelCeiling}). Clamping composite height to {clampedHeight} pixels.");
                    height = clampedHeight;
                }
                else
                {
                    int clampedWidth = (int)(pixelCeiling / height);
                    if (clampedWidth < _frameWidth) clampedWidth = _frameWidth;
                    Log.Warn($"Scroll capture composite dimension ({width}x{height}, {totalPixels} pixels) exceeds MaxCompositePixels ({pixelCeiling}). Clamping composite width to {clampedWidth} pixels.");
                    width = clampedWidth;
                }
            }

            var result = new Image<Bgra32>(width, height);
            Image<Bgra32> header = null;
            Image<Bgra32> footer = null;
            Image<Bgra32> leftBar = null;
            Image<Bgra32> rightBar = null;

            try
            {
                if (isVertical)
                {
                    if (headerH > 0 && _frameWidth > 0 && height > 0)
                    {
                        int effectiveH = Math.Min(headerH, height);
                        header = _firstFrame.Clone(c => c.Crop(new Rectangle(0, 0, _frameWidth, effectiveH)));
                        result.Mutate(ctx => ctx.DrawImage(header, new Point(0, 0), 1f));
                    }

                    if (footerH > 0 && _frameWidth > 0 && height >= footerH)
                    {
                        footer = _lastFrame.Clone(c => c.Crop(new Rectangle(0, _viewport.Bottom, _frameWidth, footerH)));
                        result.Mutate(ctx => ctx.DrawImage(footer, new Point(0, height - footerH), 1f));
                    }

                    if (leftBarW > 0 && _viewport.Height > 0)
                    {
                        leftBar = _firstFrame.Clone(c => c.Crop(new Rectangle(0, _viewport.Top, leftBarW, _viewport.Height)));
                        int curY = headerH;
                        int limitY = height - footerH;
                        int drawH = Math.Min(leftBar.Height, limitY - curY);
                        if (drawH > 0)
                        {
                            using var topSlice = leftBar.Clone(c => c.Crop(new Rectangle(0, 0, leftBarW, drawH)));
                            result.Mutate(ctx => ctx.DrawImage(topSlice, new Point(0, curY), 1f));
                            curY += drawH;
                        }
                        if (curY < limitY)
                        {
                            using var bottomRow = leftBar.Clone(c => c.Crop(new Rectangle(0, leftBar.Height - 1, leftBarW, 1)));
                            bottomRow.Mutate(ctx => ctx.Resize(leftBarW, limitY - curY));
                            result.Mutate(ctx => ctx.DrawImage(bottomRow, new Point(0, curY), 1f));
                        }
                    }

                    if (rightBarW > 0 && _viewport.Height > 0)
                    {
                        rightBar = _firstFrame.Clone(c => c.Crop(new Rectangle(_viewport.Right, _viewport.Top, rightBarW, _viewport.Height)));
                        int curY = headerH;
                        int limitY = height - footerH;
                        int drawH = Math.Min(rightBar.Height, limitY - curY);
                        if (drawH > 0)
                        {
                            using var topSlice = rightBar.Clone(c => c.Crop(new Rectangle(0, 0, rightBarW, drawH)));
                            result.Mutate(ctx => ctx.DrawImage(topSlice, new Point(_viewport.Right, curY), 1f));
                            curY += drawH;
                        }
                        if (curY < limitY)
                        {
                            using var bottomRow = rightBar.Clone(c => c.Crop(new Rectangle(0, rightBar.Height - 1, rightBarW, 1)));
                            bottomRow.Mutate(ctx => ctx.Resize(rightBarW, limitY - curY));
                            result.Mutate(ctx => ctx.DrawImage(bottomRow, new Point(_viewport.Right, curY), 1f));
                        }
                    }

                    int count = 0;
                    foreach (var segment in _segments)
                    {
                        int targetX = _viewport.Left;
                        int targetY = segment.Y - minY + _viewport.Top;
                        if (targetY < height && targetX < width && targetY + segment.Image.Height > 0 && targetX + segment.Image.Width > 0)
                        {
                            result.Mutate(ctx => ctx.DrawImage(segment.Image, new Point(targetX, targetY), 1f));
                        }
                        count++;
                        progress?.Report((double)count / _segments.Count);
                    }
                }
                else
                {
                    if (leftBarW > 0 && _frameHeight > 0 && width > 0)
                    {
                        int effectiveW = Math.Min(leftBarW, width);
                        leftBar = _firstFrame.Clone(c => c.Crop(new Rectangle(0, 0, effectiveW, _frameHeight)));
                        result.Mutate(ctx => ctx.DrawImage(leftBar, new Point(0, 0), 1f));
                    }

                    if (rightBarW > 0 && _frameHeight > 0 && width >= rightBarW)
                    {
                        rightBar = _lastFrame.Clone(c => c.Crop(new Rectangle(_viewport.Right, 0, rightBarW, _frameHeight)));
                        result.Mutate(ctx => ctx.DrawImage(rightBar, new Point(width - rightBarW, 0), 1f));
                    }

                    if (headerH > 0 && _viewport.Width > 0)
                    {
                        header = _firstFrame.Clone(c => c.Crop(new Rectangle(_viewport.Left, 0, _viewport.Width, headerH)));
                        int curX = leftBarW;
                        int limitX = width - rightBarW;
                        int drawW = Math.Min(header.Width, limitX - curX);
                        if (drawW > 0)
                        {
                            using var leftSlice = header.Clone(c => c.Crop(new Rectangle(0, 0, drawW, headerH)));
                            result.Mutate(ctx => ctx.DrawImage(leftSlice, new Point(curX, 0), 1f));
                            curX += drawW;
                        }
                        if (curX < limitX)
                        {
                            using var rightCol = header.Clone(c => c.Crop(new Rectangle(header.Width - 1, 0, 1, headerH)));
                            rightCol.Mutate(ctx => ctx.Resize(limitX - curX, headerH));
                            result.Mutate(ctx => ctx.DrawImage(rightCol, new Point(curX, 0), 1f));
                        }
                    }

                    if (footerH > 0 && _viewport.Width > 0)
                    {
                        footer = _lastFrame.Clone(c => c.Crop(new Rectangle(_viewport.Left, _viewport.Bottom, _viewport.Width, footerH)));
                        int curX = leftBarW;
                        int limitX = width - rightBarW;
                        int drawW = Math.Min(footer.Width, limitX - curX);
                        if (drawW > 0)
                        {
                            using var leftSlice = footer.Clone(c => c.Crop(new Rectangle(0, 0, drawW, footerH)));
                            result.Mutate(ctx => ctx.DrawImage(leftSlice, new Point(curX, height - footerH), 1f));
                            curX += drawW;
                        }
                        if (curX < limitX)
                        {
                            using var rightCol = footer.Clone(c => c.Crop(new Rectangle(footer.Width - 1, 0, 1, footerH)));
                            rightCol.Mutate(ctx => ctx.Resize(limitX - curX, footerH));
                            result.Mutate(ctx => ctx.DrawImage(rightCol, new Point(curX, height - footerH), 1f));
                        }
                    }

                    int count = 0;
                    foreach (var segment in _segments)
                    {
                        int targetX = segment.X - minX + _viewport.Left;
                        int targetY = _viewport.Top;
                        if (targetY < height && targetX < width && targetY + segment.Image.Height > 0 && targetX + segment.Image.Width > 0)
                        {
                            result.Mutate(ctx => ctx.DrawImage(segment.Image, new Point(targetX, targetY), 1f));
                        }
                        count++;
                        progress?.Report((double)count / _segments.Count);
                    }
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
            _previousLuma?.Dispose();
            _previousFrame?.Dispose();
            _firstFrame?.Dispose();
            _lastFrame?.Dispose();
            foreach (var r in _recentFrames)
            {
                r.Frame?.Dispose();
                r.Luma?.Dispose();
            }
            _recentFrames.Clear();
            foreach (var segment in _segments) segment.Image?.Dispose();
            _segments.Clear();
        }
    }

    internal sealed class ScrollSegment
    {
        public ScrollSegment(Image<Bgra32> image, int x, int y)
        {
            Image = image;
            X = x;
            Y = y;
        }

        public Image<Bgra32> Image { get; }
        public int X { get; }
        public int Y { get; }
    }

    internal readonly struct MovementEstimate
    {
        public MovementEstimate(int deltaX, int deltaY, bool reliable)
        {
            DeltaX = deltaX;
            DeltaY = deltaY;
            IsReliable = reliable;
        }

        public int DeltaX { get; }
        public int DeltaY { get; }
        public bool IsReliable { get; }

        public static MovementEstimate Failed => new MovementEstimate(0, 0, false);
    }

    internal sealed class FrameLuma : IDisposable
    {
        public byte[] Pixels { get; private set; }
        public int Width { get; }
        public int Height { get; }

        public FrameLuma(Image<Bgra32> image)
        {
            Width = image.Width;
            Height = image.Height;
            Pixels = new byte[Width * Height];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < Height; y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(y);
                    int offset = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        Bgra32 px = row[x];
                        Pixels[offset + x] = (byte)((px.R * 30 + px.G * 59 + px.B * 11) / 100);
                    }
                }
            });
        }

        public void Dispose()
        {
            Pixels = null;
        }
    }
}
