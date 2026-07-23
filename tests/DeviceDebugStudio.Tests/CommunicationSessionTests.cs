using System.Runtime.CompilerServices;
using DeviceDebugStudio.Core.Sessions;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class CommunicationSessionTests
{
    [Fact]
    public async Task EmptySendIsRejectedBeforeEnteringTransport()
    {
        BlockingSendTransport transport = new();
        await using CommunicationSession session = new("test", transport);
        await session.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => session.SendAsync(ReadOnlyMemory<byte>.Empty).AsTask());

        Assert.False(transport.SendStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task DisconnectCancelsPendingSend()
    {
        BlockingSendTransport transport = new();
        await using CommunicationSession session = new("test", transport);
        await session.ConnectAsync();

        Task sendTask = session.SendAsync(new byte[] { 0x41, 0x54 }).AsTask();
        await transport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
    }

    [Fact]
    public async Task TransportFaultIsForwardedToSession()
    {
        BlockingSendTransport transport = new();
        await using CommunicationSession session = new("test", transport);
        TaskCompletionSource<Exception> fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += (_, exception) => fault.TrySetResult(exception);

        await session.ConnectAsync();
        transport.RaiseFault("串口读取失败");

        Exception exception = await fault.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("串口读取失败", exception.Message, StringComparison.Ordinal);
    }

    private sealed class BlockingSendTransport : ITransport
    {
        public string DisplayName => "blocking";
        public TransportKind Kind => TransportKind.Serial;
        public TransportState State { get; private set; } = TransportState.Disconnected;
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

        public void RaiseFault(string message)
        {
            TransportState previous = State;
            State = TransportState.Faulted;
            StateChanged?.Invoke(this, new TransportStateChangedEventArgs(previous, State, message));
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = TransportState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = TransportState.Disconnected;
            return Task.CompletedTask;
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default)
        {
            SendStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async IAsyncEnumerable<TransportPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
