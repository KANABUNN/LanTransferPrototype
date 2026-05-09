using System.Diagnostics;

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
        double nextFrameTicks = Stopwatch.GetTimestamp();

        long frameNo = 0;
        long totalBytes = 0;
        long droppedScheduleFrames = 0;

        var totalStopwatch = Stopwatch.StartNew();
        var statsStopwatch = Stopwatch.StartNew();

        double lastCaptureMs = 0;
        double lastCopyMs = 0;
        double lastEncodeMs = 0;
        double lastSendMs = 0;
        double lastLoopMs = 0;

        while (!token.IsCancellationRequested)
        {
            long loopStartTimestamp = Stopwatch.GetTimestamp();
            long nowTicks = loopStartTimestamp;

            if (nowTicks < nextFrameTicks)
            {
                int delayMs = (int)Math.Max(1, (nextFrameTicks - nowTicks) * 1000.0 / Stopwatch.Frequency);
                await Task.Delay(delayMs, token);
                loopStartTimestamp = Stopwatch.GetTimestamp();
            }
            else
            {
                double lateFrames = (nowTicks - nextFrameTicks) / frameIntervalTicks;
                if (lateFrames >= 1)
                {
                    droppedScheduleFrames += (long)lateFrames;
                    nextFrameTicks = nowTicks;
                }
            }

            ScreenFrame frame = _captureService.CaptureDesktopJpeg(streamId, ++frameNo, quality, scalePercent, captureSource);
            lastCaptureMs = frame.CaptureMs;
            lastCopyMs = frame.CopyMs;
            lastEncodeMs = frame.EncodeMs;

            var sendStopwatch = Stopwatch.StartNew();
            int successClients = await sendFrameAsync(frame, token);
            sendStopwatch.Stop();
            lastSendMs = sendStopwatch.Elapsed.TotalMilliseconds;

            totalBytes += frame.ImageBytes.LongLength * Math.Max(successClients, 0);

            long loopEndTimestamp = Stopwatch.GetTimestamp();
            lastLoopMs = (loopEndTimestamp - loopStartTimestamp) * 1000.0 / Stopwatch.Frequency;

            nextFrameTicks += frameIntervalTicks;

            if (statsStopwatch.ElapsedMilliseconds >= 500)
            {
                double elapsedSeconds = Math.Max(totalStopwatch.Elapsed.TotalSeconds, 0.001);
                double actualFps = frameNo / elapsedSeconds;
                double mbps = (totalBytes * 8.0) / elapsedSeconds / 1_000_000.0;

                StatsChanged?.Invoke(new ScreenStreamStats(
                    frame.Info.StreamId,
                    frame.Info.FrameNo,
                    frame.Info.Width,
                    frame.Info.Height,
                    frame.ImageBytes.Length,
                    successClients,
                    actualFps,
                    mbps,
                    targetFps,
                    scalePercent,
                    captureSource,
                    lastCaptureMs,
                    lastCopyMs,
                    lastEncodeMs,
                    lastSendMs,
                    lastLoopMs,
                    droppedScheduleFrames));

                statsStopwatch.Restart();
            }
        }
    }
}

public sealed class ScreenVideoOptions
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public int Fps { get; init; } = 30;
    public int Quality { get; init; } = 70;

    // 100 = original size. 60 is the practical default for 30fps MJPEG.
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
    long DroppedScheduleFrames);
