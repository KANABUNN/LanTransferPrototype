using System.Diagnostics;
using System.Text.Json;
using LanShared.Contracts;

namespace LanReceiver;

public sealed partial class ReceiverForm
{
    private Process? _h264Player;
    private Stream? _h264PlayerInput;
    private long _h264Bytes;

    private void InitializeH264ReceiverFeature()
    {
        AddLog("H.264 trial receiver initialized. ffplay must be available in PATH or LAN_FFPLAY_PATH.");
    }

    private async Task HandleH264StreamStartAsync(byte[] payload)
    {
        StopH264Player();

        H264StreamInfo? info = JsonSerializer.Deserialize<H264StreamInfo>(payload);
        if (info is null)
        {
            AddLog("Invalid H.264 start payload.");
            return;
        }

        string ffplayPath = Environment.GetEnvironmentVariable("LAN_FFPLAY_PATH") ?? "ffplay";
        string args = "-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -framedrop -probesize 32 -analyzeduration 0 -f mpegts -i pipe:0";

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
        _h264Player.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AddLog("ffplay: " + e.Data);
            }
        };

        if (!_h264Player.Start())
        {
            AddLog("ffplay could not be started.");
            _h264Player = null;
            return;
        }

        _h264Player.BeginErrorReadLine();
        _h264PlayerInput = _h264Player.StandardInput.BaseStream;
        _h264Bytes = 0;
        UpdateScreenInfo($"H.264 trial started: {info.Fps}fps / scale {info.ScalePercent}% / {info.Encoder}");
        AddLog($"H.264 trial started: {info.Fps}fps / scale {info.ScalePercent}% / {info.Encoder}");
        await Task.CompletedTask;
    }

    private async Task HandleH264StreamDataAsync(byte[] payload)
    {
        if (_h264PlayerInput is null)
        {
            return;
        }

        try
        {
            await _h264PlayerInput.WriteAsync(payload.AsMemory(0, payload.Length));
            await _h264PlayerInput.FlushAsync();
            _h264Bytes += payload.Length;
            if (_h264Bytes % (1024 * 1024) < payload.Length)
            {
                UpdateScreenInfo($"H.264 trial receiving: {_h264Bytes / 1024 / 1024} MB");
            }
        }
        catch (Exception ex)
        {
            AddLog("H.264 write failed: " + ex.Message);
            StopH264Player();
        }
    }

    private void HandleH264StreamStop(byte[] payload)
    {
        StopH264Player();
        UpdateScreenInfo("H.264 trial stopped");
        AddLog("H.264 trial stopped");
    }

    private void StopH264Player()
    {
        try { _h264PlayerInput?.Dispose(); } catch { }
        _h264PlayerInput = null;

        try
        {
            if (_h264Player is not null && !_h264Player.HasExited)
            {
                _h264Player.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            try { _h264Player?.Dispose(); } catch { }
            _h264Player = null;
        }
    }
}