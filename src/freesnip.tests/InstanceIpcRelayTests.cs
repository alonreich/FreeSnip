using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    public class InstanceIpcRelayTests
    {
        [Fact]
        public void AuthoritativePipeName_MatchesSpecification()
        {
            Assert.Equal(@"Local\FreeSnip_Instance_IpcPipe", InstanceIpcRelay.PipeName);
            Assert.Equal("ACTIVATE", InstanceIpcRelay.MessageActivate);
            Assert.Equal("OPEN_FILE:", InstanceIpcRelay.MessageOpenFilePrefix);
            Assert.Equal("ACK", InstanceIpcRelay.Acknowledgment);
        }

        [Fact]
        public async Task ActivateMessage_TransmittedAndAcknowledged()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var received = new List<string>();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server = new InstanceIpcServer(msg =>
            {
                lock (received) received.Add(msg);
                tcs.TrySetResult(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            bool result = await InstanceIpcClient.SendMessageAsync(InstanceIpcRelay.MessageActivate, timeoutMs: 1500, pipeName: pipeName);

            Assert.True(result, "Client should receive ACK from server");
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            var message = completed == tcs.Task ? await tcs.Task : null;
            Assert.Equal(InstanceIpcRelay.MessageActivate, message);
        }

        [Fact]
        public async Task OpenFileMessage_TransmittedAndAcknowledged()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server = new InstanceIpcServer(msg =>
            {
                tcs.TrySetResult(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            string testPath = @"C:\TestImages\sample.png";
            string openMessage = $"{InstanceIpcRelay.MessageOpenFilePrefix}{testPath}";

            bool result = await InstanceIpcClient.SendMessageAsync(openMessage, timeoutMs: 1500, pipeName: pipeName);

            Assert.True(result, "Client should receive ACK for OPEN_FILE");
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            var message = completed == tcs.Task ? await tcs.Task : null;
            Assert.Equal(openMessage, message);
        }

        [Fact]
        public async Task SendRelayCommandAsync_NoArgs_TransmitsActivate()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server = new InstanceIpcServer(msg =>
            {
                tcs.TrySetResult(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            bool result = await InstanceIpcClient.SendRelayCommandAsync(Array.Empty<string>(), timeoutMs: 1500, pipeName: pipeName);

            Assert.True(result);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            var message = completed == tcs.Task ? await tcs.Task : null;
            Assert.Equal(InstanceIpcRelay.MessageActivate, message);
        }

        [Fact]
        public async Task SendRelayCommandAsync_FileArgs_TransmitsOpenFileWithFullPath()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server = new InstanceIpcServer(msg =>
            {
                tcs.TrySetResult(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            string relativeFile = "dummy_test_image.png";
            string expectedFullPath = Path.GetFullPath(relativeFile);

            bool result = await InstanceIpcClient.SendRelayCommandAsync(new[] { relativeFile }, timeoutMs: 1500, pipeName: pipeName);

            Assert.True(result);
            var completedFile = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            var messageFile = completedFile == tcs.Task ? await tcs.Task : null;
            Assert.Equal($"{InstanceIpcRelay.MessageOpenFilePrefix}{expectedFullPath}", messageFile);
        }

        [Fact]
        public async Task ClientTimeout_WhenNoServerListening_FailsCleanlyWithoutThrowing()
        {
            string nonExistentPipe = @"Local\FreeSnip_NonExistent_" + Guid.NewGuid().ToString("N");

            bool result = await InstanceIpcClient.SendMessageAsync(InstanceIpcRelay.MessageActivate, timeoutMs: 150, pipeName: nonExistentPipe);

            Assert.False(result, "Client should return false when no server is listening");
        }

        [Fact]
        public async Task MultipleSequentialMessages_ProcessedCorrectly()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var received = new List<string>();

            await using var server = new InstanceIpcServer(msg =>
            {
                lock (received) received.Add(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            for (int i = 0; i < 5; i++)
            {
                string msg = $"OPEN_FILE:C:\\Images\\file_{i}.png";
                bool ok = await InstanceIpcClient.SendMessageAsync(msg, timeoutMs: 1500, pipeName: pipeName);
                Assert.True(ok, $"Message {i} should be acknowledged");
            }

            Assert.Equal(5, received.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal($"OPEN_FILE:C:\\Images\\file_{i}.png", received[i]);
            }
        }

        [Fact]
        public async Task ConcurrentClientMessages_HandledGracefully()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");
            var received = new List<string>();

            await using var server = new InstanceIpcServer(msg =>
            {
                lock (received) received.Add(msg);
                return Task.CompletedTask;
            }, pipeName);

            server.Start();

            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 6; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() => InstanceIpcClient.SendMessageAsync($"OPEN_FILE:C:\\Concurrent\\img_{index}.png", timeoutMs: 3000, pipeName: pipeName)));
            }

            bool[] results = await Task.WhenAll(tasks);
            Assert.All(results, Assert.True);
            Assert.Equal(6, received.Count);
        }

        [Fact]
        public async Task ServerDisposal_CancelsListenerGracefully()
        {
            string pipeName = @"Local\FreeSnip_Test_Pipe_" + Guid.NewGuid().ToString("N");

            var server = new InstanceIpcServer(_ => Task.CompletedTask, pipeName);
            server.Start();

            await Task.Delay(50);
            await server.DisposeAsync();

            bool result = await InstanceIpcClient.SendMessageAsync(InstanceIpcRelay.MessageActivate, timeoutMs: 150, pipeName: pipeName);
            Assert.False(result);
        }
    }
}
