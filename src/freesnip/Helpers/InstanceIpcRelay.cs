using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using freesnip.foundation.core;

namespace freesnip.helpers
{
    public static class InstanceIpcRelay
    {
        public const string PipeName = @"Local\FreeSnip_Instance_IpcPipe";
        public const string MessageActivate = "ACTIVATE";
        public const string MessageOpenFilePrefix = "OPEN_FILE:";
        public const string MessageShutdown = "SHUTDOWN";
        public const string Acknowledgment = "ACK";
        public const int MaxMessageBytes = 32 * 1024;
        public static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(5);

        internal static async Task<string> ReadLineAsync(PipeStream stream, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ClientReadTimeout);
            CancellationToken readToken = timeoutCts.Token;

            using var ms = new MemoryStream(capacity: 512);
            var buffer = new byte[256];
            while (!readToken.IsCancellationRequested && stream.IsConnected)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readToken).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                for (int i = 0; i < bytesRead; i++)
                {
                    byte value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length)).Trim();
                    }

                    if (value == (byte)'\r') continue;
                    if (ms.Length >= MaxMessageBytes)
                    {
                        throw new InvalidDataException($"IPC command exceeds the {MaxMessageBytes}-byte protocol limit.");
                    }
                    ms.WriteByte(value);
                }
            }

            return ms.Length > 0
                ? Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length)).Trim()
                : null;
        }

        internal static async Task WriteLineAsync(PipeStream stream, string text, CancellationToken ct)
        {
            text ??= string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            if (bytes.Length > MaxMessageBytes + 1)
            {
                throw new InvalidDataException($"IPC command exceeds the {MaxMessageBytes}-byte protocol limit.");
            }

            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public sealed class InstanceIpcServer : IDisposable, IAsyncDisposable
    {
        private const int MaxConcurrentClients = 8;

        private readonly Func<string, Task> _messageHandler;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
        private readonly ConcurrentDictionary<int, Task> _activeClients = new();
        private Task _listenerTask;
        private int _nextClientId;
        private int _isDisposed;

        public InstanceIpcServer(Func<string, Task> messageHandler, string pipeName = InstanceIpcRelay.PipeName)
        {
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        }

        public void Start()
        {
            if (Volatile.Read(ref _isDisposed) == 1) return;
            _listenerTask ??= Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                bool slotAcquired = false;
                try
                {
                    await _clientSlots.WaitAsync(ct).ConfigureAwait(false);
                    slotAcquired = true;

                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        MaxConcurrentClients,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    int clientId = Interlocked.Increment(ref _nextClientId);
                    NamedPipeServerStream connectedServer = server;
                    server = null;

                    Task clientTask = HandleClientConnectionAsync(connectedServer, ct);
                    _activeClients[clientId] = clientTask;
                    _ = clientTask.ContinueWith(
                        _ =>
                        {
                            _activeClients.TryRemove(clientId, out _);
                            _clientSlots.Release();
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    slotAcquired = false;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    LogHelper.GetLogger(typeof(InstanceIpcServer)).Error("Error in IPC server listener loop", ex);
                    try
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                finally
                {
                    server?.Dispose();
                    if (slotAcquired) _clientSlots.Release();
                }
            }
        }

        private async Task HandleClientConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            using (server)
            {
                try
                {
                    while (!ct.IsCancellationRequested && server.IsConnected)
                    {
                        string line = await InstanceIpcRelay.ReadLineAsync(server, ct).ConfigureAwait(false);
                        if (line == null) break;

                        line = line.Trim();
                        if (line.Length == 0) continue;

                        try
                        {
                            await _messageHandler(line).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.GetLogger(typeof(InstanceIpcServer)).Error($"Error processing IPC message: {line}", ex);
                        }

                        await InstanceIpcRelay.WriteLineAsync(server, InstanceIpcRelay.Acknowledgment, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (InvalidDataException ex)
                {
                    LogHelper.GetLogger(typeof(InstanceIpcServer)).Warn("Rejected malformed/oversized IPC command", ex);
                }
                catch (Exception ex)
                {
                    LogHelper.GetLogger(typeof(InstanceIpcServer)).Error("Error handling IPC client connection", ex);
                }
                finally
                {
                    try { if (server.IsConnected) server.Disconnect(); } catch { }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

            try { _cts.Cancel(); } catch { }

            var waits = new List<Task>();
            if (_listenerTask != null) waits.Add(_listenerTask);
            waits.AddRange(_activeClients.Values);

            if (waits.Count > 0)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(waits), Task.Delay(1500)).ConfigureAwait(false);
                }
                catch { }
            }

            // Do not dispose _clientSlots here: a timed-out client task may still reach its
            // continuation and release the slot after the shutdown grace period.
            _cts.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            // Synchronous disposal never blocks the caller. Active handlers observe cancellation and
            // own/dispose their pipe instances; async callers should prefer DisposeAsync().
        }
    }

    public static class InstanceIpcClient
    {
        public static async Task<bool> SendMessageAsync(
            string message,
            int timeoutMs = 500,
            string pipeName = InstanceIpcRelay.PipeName,
            CancellationToken cancellationToken = default)
        {
            return await SendMessagesAsync(new[] { message }, timeoutMs, pipeName, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> SendMessagesAsync(
            IEnumerable<string> messages,
            int timeoutMs = 500,
            string pipeName = InstanceIpcRelay.PipeName,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeoutMs);

                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(timeoutMs, cts.Token).ConfigureAwait(false);

                foreach (var message in messages)
                {
                    if (cts.IsCancellationRequested) return false;

                    await InstanceIpcRelay.WriteLineAsync(client, message, cts.Token).ConfigureAwait(false);
                    string ack = await InstanceIpcRelay.ReadLineAsync(client, cts.Token).ConfigureAwait(false);
                    if (!string.Equals(ack?.Trim(), InstanceIpcRelay.Acknowledgment, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                BootstrapDebug.Log($"IPC client failed: {ex.Message}");
                LogHelper.GetLogger(typeof(InstanceIpcRelay)).Warn($"IPC client failed: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> SendRelayCommandAsync(
            string[] args,
            int timeoutMs = 500,
            string pipeName = InstanceIpcRelay.PipeName,
            CancellationToken cancellationToken = default)
        {
            var files = (args ?? Array.Empty<string>())
                .Where(a => !a.StartsWith("-", StringComparison.Ordinal) && !a.StartsWith("/", StringComparison.Ordinal))
                .ToList();

            if (files.Count > 0)
            {
                var messages = files.Select(f =>
                {
                    string fullPath;
                    try { fullPath = Path.GetFullPath(f); } catch { fullPath = f; }
                    return $"{InstanceIpcRelay.MessageOpenFilePrefix}{fullPath}";
                });
                return await SendMessagesAsync(messages, timeoutMs, pipeName, cancellationToken).ConfigureAwait(false);
            }

            return await SendMessagesAsync(new[] { InstanceIpcRelay.MessageActivate }, timeoutMs, pipeName, cancellationToken).ConfigureAwait(false);
        }
    }
}
