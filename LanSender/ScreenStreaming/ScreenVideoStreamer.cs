using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanSender.ScreenStreaming;

public sealed class ScreenVideoStreamer
{
    private const double WarmupSeconds = 3.0;
    private const int StatsIntervalMs = 500;
    private const double AdaptiveCooldownSeconds = 1.5;

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
        string streamId = string.IsNullOrWhiteSpace(options.StreamId)
            ? Guid.NewGuid().ToString("N")
            : options.StreamId;

        int requestedTargetFps = Math.Clamp(options.Fps, 1, 60);
        int effectiveTargetFps = requestedTargetFps;
        int requestedQuality = Math.Clamp(options.Quality, 20, 95);
        int requestedScalePercent = Math.Clamp(options.ScalePercent, 25, 100);
        string captureSource = string.IsNullOrWhiteSpace(options.CaptureSource)
            ? ScreenCaptureSource.Primary
            : options.CaptureSource;

        bool highFpsMode = requestedTargetFps >= 45;
        bool adaptiveMode = options.EnableAdaptivePerformance || highFpsMode;

        int currentQuality = requestedQuality;
        int currentScalePercent = requestedScalePercent;
        int minQuality = highFpsMode ? Math.Clamp(options.HighFpsMinQuality, 40, requestedQuality) : 20;
        int minScalePercent = highFpsMode ? Math.Clamp(options.HighFpsMinScalePercent, 40, requestedScalePercent) : 25;
        int minEffectiveFps = highFpsMode ? Math.Clamp(options.HighFpsMinEffectiveFps, 30, requestedTargetFps) : requestedTargetFps;

        if (highFpsMode)
        {
            currentQuality = Math.Min(currentQuality, options.HighFpsStartQuality);
            currentScalePercent = Math.Min(currentScalePercent, options.HighFpsStartScalePercent);
        }

        double frameIntervalTicks = Stopwatch.Frequency / (double)effectiveTargetFps;
        double frameBudgetMs = 1000.0 / effectiveTargetFps;
        double nextFrameTicks = Stopwatch.GetTimestamp();

        long frameNo = 0;
        long measuredFrameCount = 0;
        long measuredBytes = 0;
        long recentFrameCount = 0;
        long recentBytes = 0;
        long totalLateFrames = 0;
        long recentLateFrames = 0;

        var totalStopwatch = Stopwatch.StartNew();
        var statsStopwatch = Stopwatch.StartNew();
        double lastAdaptiveChangeSeconds = 0;
        string adaptiveState = highFpsMode ? "adaptive high-fps warmup" : "fixed profile";

        double lastCaptureMs = 0;
        double lastCopyMs = 0;
        double lastEncodeMs = 0;
        double lastSendMs = 0;
        double lastLoopMs = 0;
        int lastSuccessClients = 0;
        int lastFrameBytes = 0;
        int lastWidth = 0;
        int lastHeight = 0;

        bool timerResolutionRequested = TryBeginHighResolutionTimer();
        ThreadPriority previousThreadPriority = Thread.CurrentThread.Priority;

        try
        {
            TrySetCurrentThreadPriority(ThreadPriority.Highest);

            while (!token.IsCancellationRequested)
            {
                frameIntervalTicks = Stopwatch.Frequency / (double)Math.Max(effectiveTargetFps, 1);
                frameBudgetMs = 1000.0 / Math.Max(effectiveTargetFps, 1);

                double loopStartTicks = Stopwatch.GetTimestamp();
                bool isWarmingUpAtLoopStart = totalStopwatch.Elapsed.TotalSeconds < WarmupSeconds;
                double nowTicks = loopStartTicks;

                if (nowTicks < nextFrameTicks)
                {
                    await WaitUntilAsync(nextFrameTicks, token);
                    loopStartTicks = Stopwatch.GetTimestamp();
                    nowTicks = loopStartTicks;
                }
                else
                {
                    double lateByFrames = (nowTicks - nextFrameTicks) / frameIntervalTicks;
                    if (lateByFrames >= 1)
                    {
                        long lateCount = (long)Math.Floor(lateByFrames);
                        if (!isWarmingUpAtLoopStart)
                        {
                            totalLateFrames += lateCount;
                            recentLateFrames += lateCount;
                        }

                        nextFrameTicks = nowTicks;
                    }
                }

                ScreenFrame frame = _captureService.CaptureDesktopJpeg(
                    streamId,
                    ++frameNo,
                    currentQuality,
                    currentScalePercent,
                    captureSource);

                lastCaptureMs = frame.CaptureMs;
                lastCopyMs = frame.CopyMs;
                lastEncodeMs = frame.EncodeMs;
                lastFrameBytes = frame.ImageBytes.Length;
                lastWidth = frame.Info.Width;
                lastHeight = frame.Info.Height;

                var sendStopwatch = Stopwatch.StartNew();
                int successClients = await sendFrameAsync(frame, token);
                sendStopwatch.Stop();

                lastSuccessClients = successClients;
                lastSendMs = sendStopwatch.Elapsed.TotalMilliseconds;

                double loopEndTicks = Stopwatch.GetTimestamp();
                lastLoopMs = (loopEndTicks - loopStartTicks) * 1000.0 / Stopwatch.Frequency;

                bool isWarmingUpAfterFrame = totalStopwatch.Elapsed.TotalSeconds < WarmupSeconds;
                long frameBytesForClients = frame.ImageBytes.LongLength * Math.Max(successClients, 0);

                recentFrameCount++;
                recentBytes += frameBytesForClients;

                if (!isWarmingUpAfterFrame)
                {
                    measuredFrameCount++;
                    measuredBytes += frameBytesForClients;
                }

                nextFrameTicks += frameIntervalTicks;

                if (statsStopwatch.ElapsedMilliseconds >= StatsIntervalMs)
                {
                    double recentSeconds = Math.Max(statsStopwatch.Elapsed.TotalSeconds, 0.001);
                    double totalSeconds = totalStopwatch.Elapsed.TotalSeconds;
                    double measuredSeconds = Math.Max(totalSeconds - WarmupSeconds, 0.001);
                    bool isWarmingUpNow = totalSeconds < WarmupSeconds;

                    double recentFps = recentFrameCount / recentSeconds;
                    double recentMbps = (recentBytes * 8.0) / recentSeconds / 1_000_000.0;
                    double averageFps = isWarmingUpNow ? 0 : measuredFrameCount / measuredSeconds;
                    double averageMbps = isWarmingUpNow ? 0 : (measuredBytes * 8.0) / measuredSeconds / 1_000_000.0;
                    double marginMs = frameBudgetMs - lastLoopMs;

                    if (adaptiveMode && !isWarmingUpNow)
                    {
                        adaptiveState = AdaptPerformanceProfile(
                            requestedTargetFps,
                            ref effectiveTargetFps,
                            requestedQuality,
                            requestedScalePercent,
                            minQuality,
                            minScalePercent,
                            minEffectiveFps,
                            recentFps,
                            marginMs,
                            recentLateFrames,
                            totalStopwatch.Elapsed.TotalSeconds,
                            ref lastAdaptiveChangeSeconds,
                            ref currentQuality,
                            ref currentScalePercent);

                        frameIntervalTicks = Stopwatch.Frequency / (double)Math.Max(effectiveTargetFps, 1);
                        frameBudgetMs = 1000.0 / Math.Max(effectiveTargetFps, 1);
                    }

                    StatsChanged?.Invoke(new ScreenStreamStats(
                        streamId,
                        frameNo,
                        lastWidth,
                        lastHeight,
                        lastFrameBytes,
                        lastSuccessClients,
                        recentFps,
                        recentMbps,
                        requestedTargetFps,
                        currentScalePercent,
                        captureSource,
                        lastCaptureMs,
                        lastCopyMs,
                        lastEncodeMs,
                        lastSendMs,
                        lastLoopMs,
                        totalLateFrames,
                        recentFps,
                        averageFps,
                        recentMbps,
                        averageMbps,
                        marginMs,
                        recentLateFrames,
                        totalLateFrames,
                        isWarmingUpNow,
                        timerResolutionRequested,
                        currentQuality,
                        requestedQuality,
                        requestedScalePercent,
                        adaptiveMode,
                        adaptiveState,
                        effectiveTargetFps));

                    recentFrameCount = 0;
                    recentBytes = 0;
                    recentLateFrames = 0;
                    statsStopwatch.Restart();
                }
            }
        }
        finally
        {
            TrySetCurrentThreadPriority(previousThreadPriority);
            if (timerResolutionRequested)
            {
                TryEndHighResolutionTimer();
            }
        }
    }

    private static string AdaptPerformanceProfile(
        int requestedTargetFps,
        ref int effectiveTargetFps,
        int requestedQuality,
        int requestedScalePercent,
        int minQuality,
        int minScalePercent,
        int minEffectiveFps,
        double recentFps,
        double marginMs,
        long recentLateFrames,
        double elapsedSeconds,
        ref double lastAdaptiveChangeSeconds,
        ref int currentQuality,
        ref int currentScalePercent)
    {
        if (elapsedSeconds - lastAdaptiveChangeSeconds < AdaptiveCooldownSeconds)
        {
            return EffectiveState("adaptive hold", requestedTargetFps, effectiveTargetFps);
        }

        bool overloaded = recentLateFrames > 0 || marginMs < 0.5 || recentFps < effectiveTargetFps * 0.92;
        bool veryStable = recentLateFrames == 0 && marginMs > 3.0 && recentFps >= effectiveTargetFps * 0.97;

        if (overloaded)
        {
            lastAdaptiveChangeSeconds = elapsedSeconds;

            if (currentScalePercent > minScalePercent)
            {
                currentScalePercent = Math.Max(minScalePercent, currentScalePercent - 5);
                return EffectiveState($"adaptive scale down -> {currentScalePercent}%", requestedTargetFps, effectiveTargetFps);
            }

            if (currentQuality > minQuality)
            {
                currentQuality = Math.Max(minQuality, currentQuality - 5);
                return EffectiveState($"adaptive quality down -> {currentQuality}", requestedTargetFps, effectiveTargetFps);
            }

            if (effectiveTargetFps > minEffectiveFps)
            {
                effectiveTargetFps = Math.Max(minEffectiveFps, effectiveTargetFps - 5);
                return EffectiveState($"adaptive fps fallback -> {effectiveTargetFps}fps", requestedTargetFps, effectiveTargetFps);
            }

            return EffectiveState("adaptive minimum profile", requestedTargetFps, effectiveTargetFps);
        }

        if (veryStable)
        {
            lastAdaptiveChangeSeconds = elapsedSeconds;

            // Keep FPS recovery conservative. FPS first, then quality/scale.
            if (effectiveTargetFps < requestedTargetFps && marginMs > 4.0)
            {
                effectiveTargetFps = Math.Min(requestedTargetFps, effectiveTargetFps + 5);
                return EffectiveState($"adaptive fps up -> {effectiveTargetFps}fps", requestedTargetFps, effectiveTargetFps);
            }

            if (currentQuality < requestedQuality)
            {
                currentQuality = Math.Min(requestedQuality, currentQuality + 5);
                return EffectiveState($"adaptive quality up -> {currentQuality}", requestedTargetFps, effectiveTargetFps);
            }

            if (currentScalePercent < requestedScalePercent)
            {
                currentScalePercent = Math.Min(requestedScalePercent, currentScalePercent + 5);
                return EffectiveState($"adaptive scale up -> {currentScalePercent}%", requestedTargetFps, effectiveTargetFps);
            }

            return EffectiveState("adaptive full profile", requestedTargetFps, effectiveTargetFps);
        }

        return EffectiveState("adaptive stable", requestedTargetFps, effectiveTargetFps);
    }

    private static string EffectiveState(string state, int requestedTargetFps, int effectiveTargetFps)
    {
        if (effectiveTargetFps != requestedTargetFps)
        {
            return $"{state}; effective {effectiveTargetFps}fps";
        }

        return state;
    }

    private static async Task WaitUntilAsync(double targetTimestamp, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double remainingMs = (targetTimestamp - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;

            if (remainingMs <= 0.45)
            {
                break;
            }

            if (remainingMs >= 2.0)
            {
                int delayMs = Math.Max(1, (int)Math.Floor(remainingMs) - 1);
                await Task.Delay(delayMs, token);
            }
            else
            {
                await Task.Yield();
            }
        }

        while (Stopwatch.GetTimestamp() < targetTimestamp)
        {
            Thread.SpinWait(20);
        }

        token.ThrowIfCancellationRequested();
    }

    private static void TrySetCurrentThreadPriority(ThreadPriority priority)
    {
        try
        {
            Thread.CurrentThread.Priority = priority;
        }
        catch
        {
        }
    }

    private static bool TryBeginHighResolutionTimer()
    {
        try
        {
            return timeBeginPeriod(1) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryEndHighResolutionTimer()
    {
        try
        {
            _ = timeEndPeriod(1);
        }
        catch
        {
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}

public sealed class ScreenVideoOptions
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public int Fps { get; init; } = 30;
    public int Quality { get; init; } = 70;
    public int ScalePercent { get; init; } = 60;
    public string CaptureSource { get; init; } = ScreenCaptureSource.Primary;
    public bool EnableAdaptivePerformance { get; init; } = true;
    public int HighFpsStartScalePercent { get; init; } = 75;
    public int HighFpsStartQuality { get; init; } = 65;
    public int HighFpsMinScalePercent { get; init; } = 50;
    public int HighFpsMinQuality { get; init; } = 50;

    // MJPEG/GDI fallback floor. If 60fps cannot be maintained even at minimum quality/scale,
    // the effective target is reduced to avoid unstable latency.
    public int HighFpsMinEffectiveFps { get; init; } = 45;
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
    double RecentFps,
    double AverageFps,
    double RecentMbps,
    double AverageMbps,
    double MarginMs,
    long RecentLateFrames,
    long TotalLateFrames,
    bool IsWarmingUp,
    bool TimerResolutionRequested,
    int Quality,
    int RequestedQuality,
    int RequestedScalePercent,
    bool AdaptiveMode,
    string AdaptiveState,
    int EffectiveTargetFps);