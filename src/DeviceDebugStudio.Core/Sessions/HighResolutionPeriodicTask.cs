using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeviceDebugStudio.Core.Sessions;

public static class HighResolutionPeriodicTask
{
    private const uint TimerResolutionMilliseconds = 1;
    private const uint TimerResolutionSuccess = 0;
    private static readonly long MaximumSpinTicks = Math.Max(1, Stopwatch.Frequency / 2_000);

    public static Task RunAsync(
        TimeSpan interval,
        Func<CancellationToken, ValueTask<bool>> iteration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(iteration);

        long intervalTicks = Math.Max(
            1,
            checked((long)Math.Round(
                interval.TotalSeconds * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero)));
        return Task.Factory.StartNew(
            () => RunLoop(intervalTicks, iteration, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void RunLoop(
        long intervalTicks,
        Func<CancellationToken, ValueTask<bool>> iteration,
        CancellationToken cancellationToken)
    {
        Thread thread = Thread.CurrentThread;
        ThreadPriority originalPriority = thread.Priority;
        bool priorityChanged = false;
        try
        {
            thread.Priority = ThreadPriority.AboveNormal;
            priorityChanged = true;
        }
        catch (Exception exception) when (exception is ThreadStateException or PlatformNotSupportedException)
        {
        }

        bool timerResolutionChanged = OperatingSystem.IsWindows()
            && TimeBeginPeriod(TimerResolutionMilliseconds) == TimerResolutionSuccess;
        WaitHandle cancellationWaitHandle = cancellationToken.WaitHandle;
        long spinTicks = Math.Min(MaximumSpinTicks, Math.Max(1, intervalTicks / 4));

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long iterationStarted = Stopwatch.GetTimestamp();
                ValueTask<bool> operation = iteration(cancellationToken);
                bool shouldContinue = operation.IsCompletedSuccessfully
                    ? operation.Result
                    : operation.AsTask().GetAwaiter().GetResult();
                if (!shouldContinue)
                {
                    return;
                }

                WaitUntil(
                    iterationStarted + intervalTicks,
                    spinTicks,
                    cancellationToken,
                    cancellationWaitHandle);
            }
        }
        finally
        {
            if (timerResolutionChanged)
            {
                _ = TimeEndPeriod(TimerResolutionMilliseconds);
            }
            if (priorityChanged)
            {
                thread.Priority = originalPriority;
            }
        }
    }

    private static void WaitUntil(
        long targetTimestamp,
        long spinTicks,
        CancellationToken cancellationToken,
        WaitHandle cancellationWaitHandle)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            // 先用可取消的内核等待释放 CPU，最后约 0.5 ms 再短暂自旋以减少线程唤醒抖动。
            if (remainingTicks > spinTicks)
            {
                double coarseWaitMilliseconds = (remainingTicks - spinTicks) * 1_000d / Stopwatch.Frequency;
                int waitMilliseconds = (int)Math.Floor(coarseWaitMilliseconds);
                if (waitMilliseconds >= 1)
                {
                    if (cancellationWaitHandle.WaitOne(waitMilliseconds))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    Thread.Yield();
                }
                continue;
            }

            int spinCount = 0;
            while (Stopwatch.GetTimestamp() < targetTimestamp)
            {
                if ((spinCount++ & 0x3F) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Thread.SpinWait(64);
            }
            return;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}
