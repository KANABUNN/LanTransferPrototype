using System.Diagnostics;
using System.Text.Json;
using LanReceiver.ScreenStreaming;
using LanShared.Contracts;

namespace LanReceiver;

public sealed partial class ReceiverForm
{
    private Process? _h264Player;
    private Stream? _h264PlayerInput;
    private long _h264Bytes;
    private long _h264LastShownMb;
    private readonly SemaphoreSlim _h264WriteLock = new(1, 1);
    private H264ReceiverConfig _h264ReceiverConfig = H264ReceiverConfig.Load();
    private int _h264StopInProgress;
    private int _h264StreamActive;

    private void InitializeH264ReceiverFeature()
    {
        _h264ReceiverConfig = H264ReceiverConfig.Load();
        AddLog("H.264 receiver ready. ffplay path: " + FfplayPathResolver.ResolveFfplayPath(_h264ReceiverConfig.FfplayPath));
    }

    private async Task HandleH264StreamStartAsync(byte[] payload)
    {
        Interlocked.Exchange(ref _h264StopInProgress, 1);
        Interlocked.Exchange(ref _h264StreamActive, 0);
        StopH264Player();

        H264StreamInfo? info = JsonSerializer.Deserialize<H264StreamInfo>(payload);
        if (info is null)
        {
            AddLog("Invalid H.264 start payload.");
            return;
        }

        string ffplayPath = FfplayPathResolver.ResolveFfplayPath(_h264ReceiverConfig.FfplayPath);
        string args = BuildFfplayArguments(_h264ReceiverConfig, info);

        AddLog("ffplay path: " + ffplayPath);
        AddLog("ffplay args: " + args);

        var psi = new ProcessStartInfo
        {
            FileName = ffplayPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };

        _h264Player = new Process { StartInfo = psi, EnableRaisingEvents = true };
        Process player = _h264Player;
        _h264Player.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AddLog("ffplay: " + e.Data);
            }
        };
        _h264Player.Exited += (_, _) =>
        {
            if (ReferenceEquals(_h264Player, player) && Volatile.Read(ref _h264StopInProgress) == 0)
            {
                Stream? input = _h264PlayerInput;
                Interlocked.Exchange(ref _h264StreamActive, 0);
                Interlocked.Exchange(ref _h264StopInProgress, 1);
                _h264PlayerInput = null;
                _h264Player = null;
                AddLog("ffplay exited.");
                ResetScreenMonitor("H.264 player exited");
                try { input?.Dispose(); } catch { }
                try { player.Dispose(); } catch { }
            }
        };

        try
        {
            if (!_h264Player.Start())
            {
                AddLog("ffplay could not be started.");
                _h264Player = null;
                return;
            }

            Interlocked.Exchange(ref _h264StopInProgress, 0);
            Interlocked.Exchange(ref _h264StreamActive, 1);
        }
        catch (Exception ex)
        {
            AddLog("ffplay start failed: " + ex.Message);
            _h264Player = null;
            return;
        }

        _h264Player.BeginErrorReadLine();
        _h264PlayerInput = _h264Player.StandardInput.BaseStream;
        _h264Bytes = 0;
        _h264LastShownMb = 0;
        UpdateScreenInfo($"H.264 started: {info.Fps}fps / scale {info.ScalePercent}% / {info.BitrateKbps}k / {info.Encoder}");
        AddLog($"H.264 started: {info.Fps}fps / scale {info.ScalePercent}% / {info.BitrateKbps}k / {info.Encoder}");
        await Task.CompletedTask;
    }

    private async Task HandleH264StreamDataAsync(byte[] payload)
    {
        if (Volatile.Read(ref _h264StopInProgress) != 0 || Volatile.Read(ref _h264StreamActive) == 0 || _h264PlayerInput is null || payload.Length == 0)
        {
            return;
        }

        await _h264WriteLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _h264StopInProgress) != 0 || Volatile.Read(ref _h264StreamActive) == 0 || _h264PlayerInput is null)
            {
                return;
            }

            await _h264PlayerInput.WriteAsync(payload.AsMemory(0, payload.Length));
            _h264Bytes += payload.Length;

            long mb = _h264Bytes / 1024 / 1024;
            if (mb > _h264LastShownMb)
            {
                _h264LastShownMb = mb;
                UpdateScreenInfo($"H.264 receiving: {mb} MB");
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _h264StopInProgress) == 0)
            {
                AddLog("H.264 write failed: " + ex.Message);
                StopH264Player();
                ResetScreenMonitor("H.264 player stopped");
            }
        }
        finally
        {
            _h264WriteLock.Release();
        }
    }

    private void HandleH264StreamStop(byte[] payload)
    {
        Interlocked.Exchange(ref _h264StopInProgress, 1);
        Interlocked.Exchange(ref _h264StreamActive, 0);
        StopH264Player();
        ResetScreenMonitor("H.264 stopped");
        AddLog("H.264 stopped");
    }

    private void StopH264Player()
    {
        Interlocked.Exchange(ref _h264StopInProgress, 1);
        Interlocked.Exchange(ref _h264StreamActive, 0);

        Stream? input = _h264PlayerInput;
        Process? player = _h264Player;

        _h264PlayerInput = null;
        _h264Player = null;

        try
        {
            if (player is not null && !player.HasExited)
            {
                player.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            try { input?.Dispose(); } catch { }
            try { player?.Dispose(); } catch { }
        }
    }

    private static string BuildFfplayArguments(H264ReceiverConfig config, H264StreamInfo info)
    {
        var args = new List<string>
        {
            "-hide_banner",
            $"-loglevel {config.LogLevel}",
            "-fflags nobuffer",
            "-flags low_delay",
            "-framedrop",
            "-sync ext",
            "-probesize 32",
            "-analyzeduration 0",
        };

        if (config.FullScreen || info.FullScreen)
        {
            args.Add("-fs");
        }

        if (config.AlwaysOnTop || info.AlwaysOnTop)
        {
            args.Add("-alwaysontop");
        }

        args.Add("-f mpegts");
        args.Add("-i pipe:0");
        return string.Join(" ", args);
    }
}

public sealed class H264ReceiverConfig
{
    public string? FfplayPath { get; set; }
    public bool FullScreen { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public string LogLevel { get; set; } = "warning";

    private static string PathName => Path.Combine(AppContext.BaseDirectory, "h264_receiver_config.json");

    public static H264ReceiverConfig Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                H264ReceiverConfig? loaded = JsonSerializer.Deserialize<H264ReceiverConfig>(File.ReadAllText(PathName));
                if (loaded is not null)
                {
                    return loaded.Normalize();
                }
            }
        }
        catch
        {
        }

        var config = new H264ReceiverConfig().Normalize();
        config.Save();
        return config;
    }

    public H264ReceiverConfig Normalize()
    {
        LogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "warning" : LogLevel.Trim();
        return this;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(PathName, JsonSerializer.Serialize(Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
