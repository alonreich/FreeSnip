using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using freesnip.foundation.core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace freesnip.helpers
{
    public sealed class OcrRequestQueue : IDisposable, IAsyncDisposable
    {
        private readonly Func<Image, CancellationToken, Task<freesnip.foundation.interfaces.Ocr.OcrInformation>> _recognize;
        private readonly Channel<WorkItem> _channel;
        private readonly Task _worker;
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private int _disposed;

        public OcrRequestQueue(Func<Image, CancellationToken, Task<freesnip.foundation.interfaces.Ocr.OcrInformation>> recognize)
        {
            _recognize = recognize ?? throw new ArgumentNullException(nameof(recognize));
            _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _worker = Task.Run(ProcessAsync);
        }

        public Task<freesnip.foundation.interfaces.Ocr.OcrInformation> EnqueueAsync(Image image, CancellationToken cancellationToken, bool isAlreadyOwned = false)
        {
            if (image == null)
            {
                return Task.FromResult<freesnip.foundation.interfaces.Ocr.OcrInformation>(null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (isAlreadyOwned) image.Dispose();
                return Task.FromCanceled<freesnip.foundation.interfaces.Ocr.OcrInformation>(cancellationToken);
            }

            return EnqueueCoreAsync(image, cancellationToken, isAlreadyOwned);
        }

        private async Task<freesnip.foundation.interfaces.Ocr.OcrInformation> EnqueueCoreAsync(Image image, CancellationToken cancellationToken, bool isAlreadyOwned)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var completion = new TaskCompletionSource<freesnip.foundation.interfaces.Ocr.OcrInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
            Image owned = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

            try
            {
                if (isAlreadyOwned)
                {
                    owned = image;
                }
                else
                {
                    owned = await Task.Run(() => image.Clone(x => { }), linked.Token).ConfigureAwait(false);
                }

                var item = new WorkItem(owned, completion, cancellationToken);
                owned = null;

                if (!_channel.Writer.TryWrite(item))
                {
                    return await EnqueueSlowAsync(item, cancellationToken).ConfigureAwait(false);
                }

                ExecutionTrace.SetQueueDepth("Ocr", 1);
                return await completion.Task.ConfigureAwait(false);
            }
            catch
            {
                owned?.Dispose();
                throw;
            }
        }

        private async Task<freesnip.foundation.interfaces.Ocr.OcrInformation> EnqueueSlowAsync(WorkItem item, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            bool handedToWorker = false;
            try
            {
                await _channel.Writer.WriteAsync(item, linked.Token).ConfigureAwait(false);
                handedToWorker = true;
                ExecutionTrace.SetQueueDepth("Ocr", 1);
                return await item.Completion.Task.ConfigureAwait(false);
            }
            catch
            {
                if (!handedToWorker)
                {
                    item.Completion.TrySetCanceled(linked.Token);
                    item.ReleaseImage();
                }
                throw;
            }
        }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
                {
                    ExecutionTrace.SetQueueDepth("Ocr", 0);
                    if (item.CancellationToken.IsCancellationRequested)
                    {
                        item.Completion.TrySetCanceled(item.CancellationToken);
                        item.ReleaseImage();
                        continue;
                    }

                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.CancellationToken, _shutdownCts.Token);
                        var result = await _recognize(item.Image, linked.Token).ConfigureAwait(false);
                        item.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Completion.TrySetCanceled(item.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        item.Completion.TrySetException(ex);
                    }
                    finally
                    {
                        item.ReleaseImage();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                DrainPendingItems();
            }
        }

        private void DrainPendingItems()
        {
            while (_channel.Reader.TryRead(out var item))
            {
                item.Completion.TrySetCanceled(_shutdownCts.Token);
                item.ReleaseImage();
            }

            ExecutionTrace.SetQueueDepth("Ocr", 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            BeginShutdown();
            _ = _worker.ContinueWith(completed =>
            {
                if (completed.Exception != null)
                {
                    ExecutionTrace.LogException("OcrRequestQueue.Shutdown", completed.Exception.GetBaseException());
                }

                _shutdownCts.Dispose();
            }, TaskScheduler.Default);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            BeginShutdown();
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExecutionTrace.LogException("OcrRequestQueue.Shutdown", ex);
            }
            finally
            {
                _shutdownCts.Dispose();
            }
        }

        private void BeginShutdown()
        {
            try
            {
                _shutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _channel.Writer.TryComplete();
        }

        private sealed class WorkItem
        {
            private Image _image;

            public WorkItem(Image image, TaskCompletionSource<freesnip.foundation.interfaces.Ocr.OcrInformation> completion, CancellationToken cancellationToken)
            {
                _image = image;
                Completion = completion;
                CancellationToken = cancellationToken;
            }

            public Image Image => Volatile.Read(ref _image);
            public TaskCompletionSource<freesnip.foundation.interfaces.Ocr.OcrInformation> Completion { get; }
            public CancellationToken CancellationToken { get; }

            public void ReleaseImage()
            {
                Interlocked.Exchange(ref _image, null)?.Dispose();
            }
        }
    }
}
