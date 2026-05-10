using System.Diagnostics;
using System.Text;

namespace LanSender.ScreenStreaming;

public sealed class FfmpegH264Streamer : IDisposable
{
    private readonly string _ffmpegPath;
    private Process? _process;

    public FfmpegH264Streamer(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public event Action<string>? Log;

    public async Task RunAsync(
        int fps,
        int scalePercent,
        string encoder,
        Func<byte[], CancellationToken, Task> onDataAsync,
        CancellationToken token)
    {
        fps = Math.Clamp(fps, 1, 60);
        scalePercent = Math.Clamp(scalePercent, 25, 100);
        encoder = string.IsNullOrWhiteSpace(encoder) ? "libx264" : encoder;

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        int outWidth = Math.Max(16, bounds.Width * scalePercent / 100);
        int outHeight = Math.Max(16, bounds.Height * scalePercent / 100);
        outWidth -= outWidth % 2;
        outHeight -= outHeight % 2;

        string vf = scalePercent >= 100
            ? "format=yuv420p"
            : $"scale={outWidth}:{outHeight}:flags=fast_bilinear,format=yuv420p";

        string args =
            $"-hide_banner -loglevel warning " +
            $"-f gdigrab -draw_mouse 1 -framerate {fps} " +
            $"-offset_x {bounds.Left} -offset_y {bounds.Top} -video_size {bounds.Width}x{bounds.Height} -i desktop " +
            $"-an -vf {Quote(vf)} -c:v {encoder} -preset ultrafast -tune zerolatency " +
            $"-g {fps} -bf 0 -f mpegts pipe:1";

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

        if (!_process.Start())
        {
            throw new InvalidOperationException("ffmpeg process could not be started.");
        }

        _process.BeginErrorReadLine();
        Log?.Invoke($"ffmpeg started: {args}");

        byte[] buffer = new byte[32 * 1024];
        Stream output = _process.StandardOutput.BaseStream;

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
        }
    }

    public void Dispose() => Stop();

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";
}