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

        int fps = Math.Clamp(options.Fps, 1, 30);
        int intervalMs = Math.Max(1, 1000 / fps);
        int quality = Math.Clamp(options.Quality, 20, 95);

        long frameNo = 0;
        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            long loopStartMs = stopwatch.ElapsedMilliseconds;

            ScreenFrame frame = _captureService.CaptureVirtualScreenJpeg(streamId, ++frameNo, quality);
            int successClients = await sendFrameAsync(frame, token);
            totalBytes += frame.ImageBytes.LongLength * Math.Max(successClients, 0);

            double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            StatsChanged?.Invoke(new ScreenStreamStats(
                frame.Info.StreamId,
                frame.Info.FrameNo,
                frame.Info.Width,
                frame.Info.Height,
                frame.ImageBytes.Length,
                successClients,
                frame.Info.FrameNo / elapsedSeconds,
                (totalBytes * 8.0) / elapsedSeconds / 1_000_000.0));

            long elapsedInLoopMs = stopwatch.ElapsedMilliseconds - loopStartMs;
            int delayMs = Math.Max(1, intervalMs - (int)elapsedInLoopMs);
            await Task.Delay(delayMs, token);
        }
    }
}

public sealed class ScreenVideoOptions
{
    public string StreamId { get; init; } = Guid.NewGuid().ToString("N");
    public int Fps { get; init; } = 10;
    public int Quality { get; init; } = 70;
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
