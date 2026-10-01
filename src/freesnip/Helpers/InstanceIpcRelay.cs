using System;
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
        public const string LegacyPipeName = @"Local\SnapVox_Instance_IpcPipe";
        public const string MessageActivate = "ACTIVATE";
        public const string MessageOpenFilePrefix = "OPEN_FILE:";
        public const string Acknowledgment = "ACK";

        internal static async Task<string> ReadLineAsync(PipeStream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[256];
            while (!ct.IsCancellationRequested && stream.IsConnected)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        return Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    }
                    if (buffer[i] != (byte)'\r')
                    {
                        ms.WriteByte(buffer[i]);
                    }
                }
            }
            return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()).Trim() : null;
        }

        internal static async Task WriteLineAsync(PipeStream stream, string text, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public sealed class InstanceIpcServer : IDisposable, IAsyncDisposable
    {
        private readonly Func<string, Task> _messageHandler;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private Task _listenerTask;
        private int _isDisposed;

        public InstanceIpcServer(Func<string, Task> messageHandler, string pipeName = InstanceIpcRelay.PipeName)
        {
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        }

        public void Start()
        {
            if (Volatile.Read(ref _isDisposed) == 1) return;
            _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    var connectedServer = server;
                    server = null;
                    _ = HandleClientConnectionAsync(connectedServer, ct);
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

            try
            {
                _cts.Cancel();
            }
            catch { }

            if (_listenerTask != null)
            {
                try
                {
                    await Task.WhenAny(_listenerTask, Task.Delay(500)).ConfigureAwait(false);
                }
                catch { }
            }

            _cts.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

            try
            {
                _cts.Cancel();
            }
            catch { }

            _cts.Dispose();
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

                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
            else
            {
                return await SendMessagesAsync(new[] { InstanceIpcRelay.MessageActivate }, timeoutMs, pipeName, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
