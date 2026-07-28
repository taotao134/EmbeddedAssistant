using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeviceDebugStudio.Core.Sessions;
using DeviceDebugStudio.Core.Transports;
using Xunit.Abstractions;

namespace DeviceDebugStudio.Tests;

public sealed class HighResolutionPeriodicTaskTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TenMillisecondCadenceRemainsCloseToConfiguredRate()
    {
        List<long> starts = [];

        await HighResolutionPeriodicTask.RunAsync(
            TimeSpan.FromMilliseconds(10),
            _ =>
            {
                starts.Add(Stopwatch.GetTimestamp());
                return ValueTask.FromResult(starts.Count < 60);
            });

        double[] gaps = starts
            .Zip(starts.Skip(1), (previous, current) => Stopwatch.GetElapsedTime(previous, current).TotalMilliseconds)
            .Order()
            .ToArray();
        double average = gaps.Average();
        double percentile95 = gaps[(int)Math.Ceiling(gaps.Length * 0.95) - 1];
        output.WriteLine($"10 ms 调度：平均={average:F3} ms，P95={percentile95:F3} ms，最大={gaps[^1]:F3} ms");

        Assert.InRange(average, 8, 13);
        Assert.True(percentile95 < 20, $"P95 周期偏差过大：{percentile95:F3} ms");
    }

    [Fact]
    public async Task CommunicationSessionSendPathMaintainsTenMillisecondCadence()
    {
        RecordingTransport transport = new();
        await using CommunicationSession session = new("periodic-send", transport);
        await session.ConnectAsync();
        byte[] payload = [0xFD, 0x15, 0x00, 0x08, 0x01, 0x00, 0x00, 0xDF];
        int sent = 0;

        await HighResolutionPeriodicTask.RunAsync(
            TimeSpan.FromMilliseconds(10),
            async cancellationToken =>
            {
                await session.SendAsync(payload, cancellationToken: cancellationToken, sentAsHex: true).ConfigureAwait(false);
                return Interlocked.Increment(ref sent) < 60;
            });

        double[] gaps = transport.SendTimestamps
            .Zip(
                transport.SendTimestamps.Skip(1),
                (previous, current) => Stopwatch.GetElapsedTime(previous, current).TotalMilliseconds)
            .Order()
            .ToArray();
        double average = gaps.Average();
        double percentile95 = gaps[(int)Math.Ceiling(gaps.Length * 0.95) - 1];
        output.WriteLine($"10 ms 会话发送：平均={average:F3} ms，P95={percentile95:F3} ms，最大={gaps[^1]:F3} ms");

        Assert.Equal(60, transport.SendTimestamps.Count);
        Assert.InRange(average, 8, 13);
        Assert.True(percentile95 < 20, $"会话发送 P95 周期偏差过大：{percentile95:F3} ms");
    }

    [Fact]
    public async Task IterationWorkDoesNotAccumulateOntoConfiguredInterval()
    {
        List<long> starts = [];

        await HighResolutionPeriodicTask.RunAsync(
            TimeSpan.FromMilliseconds(40),
            _ =>
            {
                starts.Add(Stopwatch.GetTimestamp());
                Thread.Sleep(20);
                return ValueTask.FromResult(starts.Count < 5);
            });

        Assert.Equal(5, starts.Count);
        TimeSpan total = Stopwatch.GetElapsedTime(starts[0], starts[^1]);
        Assert.True(total < TimeSpan.FromMilliseconds(220), $"总周期耗时过长：{total.TotalMilliseconds:F1} ms");
        foreach ((long previous, long current) in starts.Zip(starts.Skip(1)))
        {
            TimeSpan gap = Stopwatch.GetElapsedTime(previous, current);
            Assert.True(gap >= TimeSpan.FromMilliseconds(25), $"检测到追赶式突发发送：{gap.TotalMilliseconds:F1} ms");
        }
    }

    [Fact]
    public async Task CancellationInterruptsLongPeriodicWaitImmediately()
    {
        using CancellationTokenSource source = new();
        TaskCompletionSource firstIteration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task runner = HighResolutionPeriodicTask.RunAsync(
            TimeSpan.FromSeconds(5),
            _ =>
            {
                firstIteration.TrySetResult();
                return ValueTask.FromResult(true);
            },
            source.Token);
        await firstIteration.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Stopwatch stopwatch = Stopwatch.StartNew();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"取消耗时过长：{stopwatch.ElapsedMilliseconds} ms");
    }

    private sealed class RecordingTransport : ITransport
    {
        public string DisplayName => "recording";
        public TransportKind Kind => TransportKind.Udp;
        public TransportState State { get; private set; } = TransportState.Disconnected;
        public List<long> SendTimestamps { get; } = [];

        public event EventHandler<TransportStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
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

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            string? target = null,
            CancellationToken cancellationToken = default)
        {
            SendTimestamps.Add(Stopwatch.GetTimestamp());
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TransportPacket> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
