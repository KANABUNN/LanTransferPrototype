using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanSender.ScreenStreaming;

public sealed class ScreenVideoStreamer
{
    private readonly ScreenCaptureService _captureService;

    public ScreenVideoStreamer(ScreenCaptureService captureService)
    {
        _captureService = captureService;
    }

    public event Action<ScreenStreamStats>? StatsChanged;

    public async Task RunAsync(
        ScreenVideoOptions options,
        Func<ScreenFrame, CancellationToken, Task<int>> sendFrameAsync,
        CancellationToken token)
    {
        using HighResolutionTimerScope timerScope = HighResolutionTimerScope.TryStart();

        string streamId = string.IsNullOrWhiteSpace(options.StreamId)
            ? Guid.NewGuid().ToString("N")
            : options.StreamId;

        int targetFps = Math.Clamp(options.Fps, 1, 60);
        int quality = Math.Clamp(options.Quality, 20, 95);
        int scalePercent = Math.Clamp(options.ScalePercent, 25, 100);
        string captureSource = string.IsNullOrWhiteSpace(options.CaptureSource)
            ? ScreenCaptureSource.Primary
            : options.CaptureSource;

        double frameIntervalTicks = Stopwatch.Frequency / (double)targetFps;
        double frameBudgetMs = 1000.0 / targetFps;

        long streamStartTicks = Stopwatch.GetTimestamp();
        double nextFrameTicks = streamStartTicks;

        long frameNo = 0;
        long totalBytes = 0;
        long droppedScheduleFrames = 0;

        long statsStartTicks = streamStartTicks;
        long statsFrameStart = 0;
        long statsBytesStart = 0;

        double lastCaptureMs = 0;
        double lastCopyMs = 0;
        double lastEncodeMs = 0;
        double lastSendMs = 0;
        double lastLoopMs = 0;
        int lastSuccessClients = 0;
        ScreenFrame? lastFrameForStats = null;

        while (!token.IsCancellationRequested)
        {
            await PreciseDelayUntilAsync(nextFrameTicks, token);

            long loopStartTicks = Stopwatch.GetTimestamp();

            ScreenFrame frame = _captureService.CaptureDesktopJpeg(streamId, ++frameNo, quality, scalePercent, captureSource);
            lastFrameForStats = frame;
            lastCaptureMs = frame.CaptureMs;
            lastCopyMs = frame.CopyMs;
            lastEncodeMs = frame.EncodeMs;

            long sendStartTicks = Stopwatch.GetTimestamp();
            int successClients = await sendFrameAsync(frame, token);
            long sendEndTicks = Stopwatch.GetTimestamp();

            lastSuccessClients = successClients;
            lastSendMs = TicksToMilliseconds(sendEndTicks - sendStartTicks);
            totalBytes += frame.ImageBytes.LongLength * Math.Max(successClients, 0);

            long loopEndTicks = Stopwatch.GetTimestamp();
            lastLoopMs = TicksToMilliseconds(loopEndTicks - loopStartTicks);

            nextFrameTicks += frameIntervalTicks;

            if (loopEndTicks > nextFrameTicks)
            {
                long missedFrames = (long)Math.Floor((loopEndTicks - nextFrameTicks) / frameIntervalTicks) + 1;
                if (missedFrames > 0)
                {
                    droppedScheduleFrames += missedFrames;
                    nextFrameTicks += missedFrames * frameIntervalTicks;
                }
            }

            long nowTicks = Stopwatch.GetTimestamp();
            double statsElapsedMs = TicksToMilliseconds(nowTicks - statsStartTicks);

            if (statsElapsedMs >= 500 && lastFrameForStats is not null)
            {
                double statsElapsedSeconds = Math.Max(statsElapsedMs / 1000.0, 0.001);
                double totalElapsedSeconds = Math.Max(TicksToMilliseconds(nowTicks - streamStartTicks) / 1000.0, 0.001);

                long framesInWindow = frameNo - statsFrameStart;
                long bytesInWindow = totalBytes - statsBytesStart;

                double windowFps = framesInWindow / statsElapsedSeconds;
                double averageFps = frameNo / totalElapsedSeconds;
                double windowMbps = (bytesInWindow * 8.0) / statsElapsedSeconds / 1_000_000.0;
                double remainingBudgetMs = frameBudgetMs - lastLoopMs;

                StatsChanged?.Invoke(new ScreenStreamStats(
                    lastFrameForStats.Info.StreamId,
                    lastFrameForStats.Info.FrameNo,
                    lastFrameForStats.Info.Width,
                    lastFrameForStats.Info.Height,
                    lastFrameForStats.ImageBytes.Length,
                    lastSuccessClients,
                    windowFps,
                    windowMbps,
                    targetFps,
                    scalePercent,
                    captureSource,
                    lastCaptureMs,
                    lastCopyMs,
                    lastEncodeMs,
                    lastSendMs,
                    lastLoopMs,
                    droppedScheduleFrames,
                    averageFps,
                    frameBudgetMs,
                    remainingBudgetMs,
                    timerScope.Enabled));

                statsStartTicks = nowTicks;
                statsFrameStart = frameNo;
                statsBytesStart = totalBytes;
            }
        }
    }

    private static async Task PreciseDelayUntilAsync(double targetTicks, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            long nowTicks = Stopwatch.GetTimestamp();
            double remainingMs = (targetTicks - nowTicks) * 1000.0 / Stopwatch.Frequency;

            if (remainingMs <= 0)
            {
                return;
            }

            if (remainingMs > 4.0)
            {
                await Task.Delay(Math.Max(1, (int)(remainingMs - 2.0)), token);
                continue;
            }

            if (remainingMs > 1.0)
            {
                await Task.Delay(1, token);
                continue;
            }

            if (remainingMs > 0.20)
            {
                Thread.SpinWait(80);
                continue;
            }

            return;
        }
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class HighResolutionTimerScope : IDisposable
    {
        private HighResolutionTimerScope(bool enabled)
        {
            Enabled = enabled;
        }

        public bool Enabled { get; }

        public static HighResolutionTimerScope TryStart()
        {
            try
            {
                return new HighResolutionTimerScope(timeBeginPeriod(1) == 0);
            }
            catch
            {
                return new HighResolutionTimerScope(false);
            }
        }

        public void Dispose()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                timeEndPeriod(1);
            }
            catch
            {
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);
    }
}

public sealed class ScreenVideoOptions
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public int Fps { get; init; } = 30;
    public int Quality { get; init; } = 70;

    // 100 = original size. 60 is practical when copy/encode cost is high.
    public int ScalePercent { get; init; } = 60;

    // Primary is faster than Virtual when the sender has multiple displays.
    public string CaptureSource { get; init; } = ScreenCaptureSource.Primary;
}

public static class ScreenCaptureSource
{
    public const string Primary = "Primary";
    public const string Virtual = "Virtual";
}

public sealed record ScreenStreamStats(
    string StreamId,
    long FrameNo,
    int Width,
    int Height,
    int LastFrameBytes,
    int SuccessClients,
    double Fps,
    double Mbps,
    int TargetFps,
    int ScalePercent,
    string CaptureSource,
    double CaptureMs,
    double CopyMs,
    double EncodeMs,
    double SendMs,
    double LoopMs,
    long DroppedScheduleFrames,
    double AverageFps,
    double FrameBudgetMs,
    double RemainingBudgetMs,
    bool HighResolutionTimerEnabled);
internal sealed class ScreenStreamingPriorityScope : IDisposable
{
    private readonly Process _process;
    private readonly ProcessPriorityClass _originalProcessPriority;
    private readonly ThreadPriority _originalThreadPriority;
    private bool _disposed;

    private ScreenStreamingPriorityScope(
        Process process,
        ProcessPriorityClass originalProcessPriority,
        ThreadPriority originalThreadPriority)
    {
        _process = process;
        _originalProcessPriority = originalProcessPriority;
        _originalThreadPriority = originalThreadPriority;
    }

    public static ScreenStreamingPriorityScope TryEnter()
    {
        Process process = Process.GetCurrentProcess();
        ProcessPriorityClass originalProcessPriority = process.PriorityClass;
        ThreadPriority originalThreadPriority = Thread.CurrentThread.Priority;

        try
        {
            if (process.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal)
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
        }
        catch
        {
        }

        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
        catch
        {
        }

        return new ScreenStreamingPriorityScope(process, originalProcessPriority, originalThreadPriority);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Thread.CurrentThread.Priority = _originalThreadPriority;
        }
        catch
        {
        }

        try
        {
            _process.PriorityClass = _originalProcessPriority;
        }
        catch
        {
        }
    }
}