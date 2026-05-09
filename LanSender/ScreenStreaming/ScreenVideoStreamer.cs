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

        int fps = Math.Clamp(options.Fps, 1, 60);
        int quality = Math.Clamp(options.Quality, 20, 95);

        double intervalTicksDouble = (double)Stopwatch.Frequency / fps;
        long nextFrameTicks = Stopwatch.GetTimestamp();

        long frameNo = 0;
        long totalBytes = 0;
        int lastSuccessClients = 0;

        var totalStopwatch = Stopwatch.StartNew();
        long lastStatsTicks = Stopwatch.GetTimestamp();

        while (!token.IsCancellationRequested)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            long waitTicks = nextFrameTicks - nowTicks;

            if (waitTicks > 0)
            {
                int waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                if (waitMs > 1)
                {
                    await Task.Delay(waitMs, token);
                }
                else
                {
                    await Task.Yield();
                }
            }

            token.ThrowIfCancellationRequested();

            ScreenFrame frame = _captureService.CaptureVirtualScreenJpeg(streamId, ++frameNo, quality);
            int successClients = await sendFrameAsync(frame, token);
            lastSuccessClients = successClients;
            totalBytes += frame.ImageBytes.LongLength * Math.Max(successClients, 0);

            long afterSendTicks = Stopwatch.GetTimestamp();
            double elapsedSeconds = Math.Max(totalStopwatch.Elapsed.TotalSeconds, 0.001);
            double effectiveFps = frameNo / elapsedSeconds;
            double mbps = (totalBytes * 8.0) / elapsedSeconds / 1_000_000.0;

            if (frameNo == 1 || (afterSendTicks - lastStatsTicks) >= Stopwatch.Frequency / 2)
            {
                lastStatsTicks = afterSendTicks;
                StatsChanged?.Invoke(new ScreenStreamStats(
                    frame.Info.StreamId,
                    frame.Info.FrameNo,
                    frame.Info.Width,
                    frame.Info.Height,
                    frame.ImageBytes.Length,
                    lastSuccessClients,
                    effectiveFps,
                    mbps));
            }

            long nextTicks = nextFrameTicks + (long)Math.Round(intervalTicksDouble);

            // If capture or send takes too long, do not queue old frames.
            // Reset the schedule so the next loop always targets the newest screen state.
            if (afterSendTicks - nextTicks > (long)Math.Round(intervalTicksDouble))
            {
                nextFrameTicks = afterSendTicks + (long)Math.Round(intervalTicksDouble);
            }
            else
            {
                nextFrameTicks = nextTicks;
            }
        }
    }
}

public sealed class ScreenVideoOptions
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public int Fps { get; init; } = 30;
    public int Quality { get; init; } = 75;
}

public sealed record ScreenStreamStats(
    string StreamId,
    long FrameNo,
    int Width,
    int Height,
    int LastFrameBytes,
    int SuccessClients,
    double Fps,
    double Mbps);