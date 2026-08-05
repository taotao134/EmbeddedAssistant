using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class SerialPortTransportTests
{
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    public void TransientReadPolicyClassifiesTimeoutAndCancellation(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(SerialPortTransport.TransientReadRetryPolicy.IsTransient(exception));
    }

    [Fact]
    public void TransientReadPolicyRecognizesOperationAbortedHResult()
    {
        IOException exception = new("驱动中止重叠读取", new IOException("inner", new Win32Exception(995)));
        Assert.True(SerialPortTransport.TransientReadRetryPolicy.IsTransient(exception));
    }

    [Fact]
    public void TransientReadPolicyRecognizesNativeOperationAborted()
    {
        IOException exception = new("驱动中止读取", new Win32Exception(995));
        Assert.True(SerialPortTransport.TransientReadRetryPolicy.IsTransient(exception));
    }

    [Fact]
    public void TransientReadPolicyRejectsUnrelatedIoErrors()
    {
        Assert.False(SerialPortTransport.TransientReadRetryPolicy.IsTransient(new IOException("端口未找到")));
        Assert.False(SerialPortTransport.TransientReadRetryPolicy.IsTransient(new UnauthorizedAccessException("占用")));
        Assert.False(SerialPortTransport.TransientReadRetryPolicy.IsTransient(new IOException("参数错误", new Win32Exception(87))));
        Assert.False(SerialPortTransport.TransientReadRetryPolicy.IsTransient(new InvalidOperationException("未知状态")));
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(3, 200)]
    [InlineData(4, 400)]
    [InlineData(5, 800)]
    [InlineData(10, 800)]
    public void TransientReadPolicyUsesBackoffSequence(int retryCount, int expectedDelayMilliseconds)
    {
        Assert.Equal(expectedDelayMilliseconds, SerialPortTransport.TransientReadRetryPolicy.GetRetryDelay(retryCount));
    }

    [Fact]
    public void HeartbeatPayloadRoundTrips()
    {
        byte[] payload = SerialPortTransport.BuildHeartbeatPayload(
            portOpen: true,
            lastSuccessfulReadUnixMs: 123456789,
            readLoopActivityUnixMs: 123456700,
            receivedBytes: 987654,
            timeoutStreak: 7,
            transientRetryCount: 3);

        Assert.True(SerialPortTransport.TryParseWorkerHeartbeat(payload, out SerialPortTransport.WorkerHeartbeatSnapshot? snapshot));
        Assert.NotNull(snapshot);
        Assert.True(snapshot.PortOpen);
        Assert.Equal(123456789, snapshot.LastSuccessfulReadUnixMs);
        Assert.Equal(123456700, snapshot.ReadLoopActivityUnixMs);
        Assert.Equal(987654, snapshot.ReceivedBytes);
        Assert.Equal(7, snapshot.TimeoutStreak);
        Assert.Equal(3, snapshot.TransientRetryCount);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
    }

    [Fact]
    public void HeartbeatPayloadRejectsMalformedContent()
    {
        Assert.False(SerialPortTransport.TryParseWorkerHeartbeat(Encoding.UTF8.GetBytes("garbage"), out _));
        Assert.False(SerialPortTransport.TryParseWorkerHeartbeat(Encoding.UTF8.GetBytes("pid=1;open=2;lastReadMs=x;readLoopMs=1;bytes=1;timeouts=1;retries=1"), out _));
        Assert.False(SerialPortTransport.TryParseWorkerHeartbeat(Encoding.UTF8.GetBytes("pid=1;open=1;lastReadMs=1;readLoopMs=1;bytes=1;timeouts=1"), out _));
        Assert.False(SerialPortTransport.TryParseWorkerHeartbeat([], out _));
    }

    [Fact]
    public void WorkerHealthDetectsStalledReadEvenWhenHeartbeatIsFresh()
    {
        long now = 10_000;

        string? reason = SerialPortTransport.GetWorkerFailureReason(
            now,
            lastHeartbeatUnixMilliseconds: now - 500,
            lastReadActivityUnixMilliseconds: now - 4_000);

        Assert.Contains("读取循环疑似卡死", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerHealthDetectsMissingHeartbeat()
    {
        long now = 10_000;

        string? reason = SerialPortTransport.GetWorkerFailureReason(
            now,
            lastHeartbeatUnixMilliseconds: now - 4_000,
            lastReadActivityUnixMilliseconds: now - 500);

        Assert.Contains("心跳超时", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventPipeWriteTimesOutUnderBackpressure()
    {
        string pipeName = $"DeviceDebugStudio.Test.Event.{Guid.NewGuid():N}";
        using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        Task serverConnection = server.WaitForConnectionAsync();
        await client.ConnectAsync(3000);
        await serverConnection;
        using SemaphoreSlim writeLock = new(1, 1);

        byte[] payload = new byte[256 * 1024];
        Exception exception = await Assert.ThrowsAsync<IOException>(() =>
            SerialPortTransport.WriteEventFrameWithTimeoutAsync(
                client,
                writeLock,
                3,
                payload,
                "测试连接",
                CancellationToken.None,
                2000));

        Assert.Contains("写入超时", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLoopRecoversAfterTransientAbort()
    {
        FakeReadSource source = new(
            () => throw new Win32Exception(995),
            () => 4,
            () => 0);
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token);

        await WaitUntilAsync(() => eventPipe.Length >= 9, TimeSpan.FromSeconds(3));
        (byte kind, byte[] payload) = eventPipe.ReadFrame();
        Assert.Equal(3, kind);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, payload);
        Assert.Equal(1, Interlocked.Read(ref stats.TransientRetryCount));
        Assert.Equal(4, Interlocked.Read(ref stats.TotalBytes));
        Assert.Equal(0, Interlocked.Read(ref stats.TimeoutStreak));

        cancellation.Cancel();
        await readTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadLoopFailsAfterConsecutiveTransientAborts()
    {
        FakeReadSource source = new(
            () => throw new Win32Exception(995),
            () => throw new Win32Exception(995),
            () => throw new Win32Exception(995),
            () => throw new Win32Exception(995),
            () => throw new Win32Exception(995));
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token);

        Exception exception = await Assert.ThrowsAsync<IOException>(() => readTask);
        Assert.Contains("连续失败 5 次", exception.Message, StringComparison.Ordinal);
        Assert.Equal(5, Interlocked.Read(ref stats.TransientRetryCount));
    }

    [Fact]
    public async Task ReadLoopTracksTimeoutStreakAndClearsOnSuccess()
    {
        using ManualResetEventSlim allowSuccess = new();
        TimeoutStreakReadSource source = new(allowSuccess);
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token);

        await WaitUntilAsync(() => Interlocked.Read(ref stats.TimeoutStreak) == 2, TimeSpan.FromSeconds(3));
        allowSuccess.Set();
        await WaitUntilAsync(() => eventPipe.Length >= 6, TimeSpan.FromSeconds(3));
        Assert.Equal(0, Interlocked.Read(ref stats.TimeoutStreak));
        Assert.Equal(1, Interlocked.Read(ref stats.TotalBytes));
        Assert.Equal(0, Interlocked.Read(ref stats.TransientRetryCount));

        cancellation.Cancel();
        await readTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadLoopExitsOnCancellation()
    {
        FakeReadSource source = new(() => 0);
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token);

        await Task.Delay(100);
        Assert.False(readTask.IsCompleted);
        cancellation.Cancel();
        await readTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadLoopExitsWhenReadThrowsAfterCancellationRequested()
    {
        FakeReadSource source = new(
            () => throw new OperationCanceledException(),
            () => throw new TimeoutException());
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token);

        await Task.Delay(50);
        cancellation.Cancel();
        await readTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadLoopMarksActivityBeforeABlockingRead()
    {
        using ManualResetEventSlim readEntered = new();
        using ManualResetEventSlim releaseRead = new();
        BlockingReadSource source = new(readEntered, releaseRead);
        SerialPortTransport.SerialReadStats stats = new();
        using ThreadSafeMemoryStream eventPipe = new();
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource cancellation = new();

        Task readTask = Task.Run(() => SerialPortTransport.ReadSerialInWorkerAsync(
            source,
            eventPipe,
            writeLock,
            stats,
            "测试连接",
            cancellation.Token));

        Assert.True(readEntered.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(Interlocked.Read(ref stats.ReadLoopActivityUnixMs) > 0);
        cancellation.Cancel();
        releaseRead.Set();
        await readTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("等待条件超时。");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class FakeReadSource : SerialPortTransport.ISerialPortReadSource
    {
        private readonly Queue<Func<int>> _behaviors = [];
        private readonly byte[] _payload = [0x11, 0x22, 0x33, 0x44];

        public FakeReadSource(params Func<int>[] behaviors)
        {
            foreach (Func<int> behavior in behaviors)
            {
                _behaviors.Enqueue(behavior);
            }
        }

        public bool IsOpen { get; set; } = true;

        public int ReadTimeout => 250;

        public int Read(byte[] buffer, int offset, int count)
        {
            int result = _behaviors.Count > 0 ? _behaviors.Dequeue()() : 0;
            if (result > 0)
            {
                Array.Copy(_payload, 0, buffer, offset, Math.Min(result, _payload.Length));
            }

            return result;
        }
    }

    private sealed class BlockingReadSource(
        ManualResetEventSlim readEntered,
        ManualResetEventSlim releaseRead) : SerialPortTransport.ISerialPortReadSource
    {
        public bool IsOpen => true;

        public int ReadTimeout => 250;

        public int Read(byte[] buffer, int offset, int count)
        {
            readEntered.Set();
            releaseRead.Wait();
            return 0;
        }
    }

    private sealed class TimeoutStreakReadSource(ManualResetEventSlim allowSuccess) : SerialPortTransport.ISerialPortReadSource
    {
        private int _readCount;

        public bool IsOpen => true;

        public int ReadTimeout => 250;

        public int Read(byte[] buffer, int offset, int count)
        {
            int readCount = Interlocked.Increment(ref _readCount);
            if (readCount <= 2)
            {
                throw new TimeoutException();
            }

            if (readCount == 3)
            {
                allowSuccess.Wait();
                buffer[offset] = 0x11;
                return 1;
            }

            return 0;
        }
    }

    private sealed class ThreadSafeMemoryStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (this)
            {
                base.Write(buffer, offset, count);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lock (this)
            {
                base.Write(buffer, offset, count);
            }

            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (this)
            {
                base.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
            lock (this)
            {
                base.Flush();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Length
        {
            get
            {
                lock (this)
                {
                    return base.Length;
                }
            }
        }

        public override long Position
        {
            get
            {
                lock (this)
                {
                    return base.Position;
                }
            }
            set
            {
                lock (this)
                {
                    base.Position = value;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (this)
            {
                return base.Read(buffer, offset, count);
            }
        }
    }
}

internal static class TestFrameExtensions
{
    public static (byte Kind, byte[] Payload) ReadFrame(this MemoryStream stream)
    {
        byte[] header = new byte[5];
        stream.Position = 0;
        stream.Read(header, 0, header.Length);
        int length = BitConverter.ToInt32(header, 1);
        byte[] payload = new byte[length];
        if (length > 0)
        {
            stream.Read(payload, 0, length);
        }

        return (header[0], payload);
    }
}
