using System.Diagnostics;
using System.Text;

namespace LanSender.ScreenStreaming;

public sealed class FfmpegH264Streamer : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private long _bytesWritten;
    private readonly Stopwatch _stopwatch = new();

    public FfmpegH264Streamer(string? ffmpegPath = null)
    {
        _ffmpegPath = FfmpegPathResolver.ResolveFfmpegPath(ffmpegPath);
    }

    public event Action<string>? Log;
    public event Action<H264SenderStats>? StatsChanged;

    public async Task RunAsync(
        H264SenderSettings settings,
        Func<byte[], CancellationToken, Task> onDataAsync,
        CancellationToken token)
    {
        settings = settings.Normalize();

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        int outWidth = MakeEven(Math.Max(16, bounds.Width * settings.ScalePercent / 100));
        int outHeight = MakeEven(Math.Max(16, bounds.Height * settings.ScalePercent / 100));

        string vf = settings.ScalePercent >= 100
            ? "format=yuv420p"
            : $"scale={outWidth}:{outHeight}:flags=fast_bilinear,format=yuv420p";

        string args = BuildArguments(settings, bounds, vf);
        Log?.Invoke($"ffmpeg path: {_ffmpegPath}");
        Log?.Invoke($"ffmpeg args: {args}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Log?.Invoke(e.Data);
            }
        };

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException("ffmpeg process could not be started.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"ffmpeg could not be started. path={_ffmpegPath}. {ex.Message}", ex);
        }

        _process.BeginErrorReadLine();
        _bytesWritten = 0;
        _stopwatch.Restart();

        byte[] buffer = new byte[Math.Clamp(settings.ChunkSizeBytes, 16 * 1024, 512 * 1024)];
        Stream output = _process.StandardOutput.BaseStream;
        var statsStopwatch = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                {
                    break;
                }

                byte[] chunk = buffer.AsSpan(0, read).ToArray();
                await onDataAsync(chunk, token);
                _bytesWritten += read;

                if (statsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    double seconds = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
                    StatsChanged?.Invoke(new H264SenderStats(_bytesWritten, (_bytesWritten * 8.0) / seconds / 1_000_000.0));
                    statsStopwatch.Restart();
                }
            }

            if (_process.HasExited && _process.ExitCode != 0 && !token.IsCancellationRequested)
            {
                throw new InvalidOperationException($"ffmpeg exited with code {_process.ExitCode}.");
            }
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
            _stopwatch.Stop();
        }
    }

    public void Dispose() => Stop();

    private static string BuildArguments(H264SenderSettings settings, Rectangle bounds, string vf)
    {
        string encoder = settings.Encoder.Trim();
        string encoderOptions = BuildEncoderOptions(settings);
        string input =
            $"-f gdigrab -draw_mouse 1 -framerate {settings.Fps} " +
            $"-offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -i desktop";

        return
            $"-hide_banner -loglevel {settings.LogLevel} -fflags nobuffer -flags low_delay " +
            input + " " +
            $"-an -vf {Quote(vf)} -c:v {encoder} {encoderOptions} " +
            $"-g {settings.Fps} -bf 0 -muxdelay 0 -muxpreload 0 -f mpegts pipe:1";
    }

    private static string BuildEncoderOptions(H264SenderSettings settings)
    {
        string encoder = settings.Encoder.Trim().ToLowerInvariant();
        int bitrate = settings.BitrateKbps;
        int bufsize = Math.Max(bitrate, settings.BitrateKbps * 2);

        if (encoder.Contains("nvenc"))
        {
            return $"-preset p1 -tune ull -rc cbr -b:v {bitrate}k -maxrate {bitrate}k -bufsize {bufsize}k";
        }

        if (encoder.Contains("qsv"))
        {
            return $"-preset veryfast -b:v {bitrate}k -maxrate {bitrate}k -bufsize {bufsize}k";
        }

        if (encoder.Contains("mf") || encoder.Contains("amf"))
        {
            return $"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {bufsize}k";
        }

        return $"-preset {settings.Preset} -tune zerolatency -b:v {bitrate}k -maxrate {bitrate}k -bufsize {bufsize}k -x264-params keyint={settings.Fps}:min-keyint={settings.Fps}:scenecut=0";
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";
}

public sealed class H264SenderSettings
{
    public string? FfmpegPath { get; set; }
    public string Encoder { get; set; } = "libx264";
    public int Fps { get; set; } = 60;
    public int ScalePercent { get; set; } = 100;
    public int BitrateKbps { get; set; } = 12000;
    public string Preset { get; set; } = "ultrafast";
    public string LogLevel { get; set; } = "warning";
    public int ChunkSizeBytes { get; set; } = 64 * 1024;

    public H264SenderSettings Normalize()
    {
        return new H264SenderSettings
        {
            FfmpegPath = FfmpegPath,
            Encoder = string.IsNullOrWhiteSpace(Encoder) ? "libx264" : Encoder.Trim(),
            Fps = Math.Clamp(Fps, 1, 60),
            ScalePercent = Math.Clamp(ScalePercent, 25, 100),
            BitrateKbps = Math.Clamp(BitrateKbps, 500, 100000),
            Preset = string.IsNullOrWhiteSpace(Preset) ? "ultrafast" : Preset.Trim(),
            LogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "warning" : LogLevel.Trim(),
            ChunkSizeBytes = Math.Clamp(ChunkSizeBytes, 16 * 1024, 512 * 1024),
        };
    }
}

public sealed record H264SenderStats(long Bytes, double Mbps);