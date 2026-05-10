using System.Text.Json;
using System.Linq;
using LanSender.ScreenStreaming;
using LanShared.Contracts;
using LanShared.Protocol;

namespace LanSender;

// v32 dynamic object compatibility

public sealed partial class SenderForm
{
    private Button? _startH264AllButton;
    private Button? _startH264SelectedButton;
    private Button? _stopH264Button;
    private CancellationTokenSource? _h264Cts;
    private FfmpegH264Streamer? _h264Streamer;

    private void InitializeH264Feature()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
        };

        panel.Controls.Add(new Label
        {
            Text = "H.264 trial:",
            AutoSize = true,
            Padding = new Padding(0, 7, 8, 0),
        });

        _startH264AllButton = new Button { Text = "H264 all", Width = 100 };
        _startH264SelectedButton = new Button { Text = "H264 selected", Width = 120 };
        _stopH264Button = new Button { Text = "Stop H264", Width = 100, Enabled = false };

        _startH264AllButton.Click += async (_, _) => await StartH264TrialAsync(selectedOnly: false);
        _startH264SelectedButton.Click += async (_, _) => await StartH264TrialAsync(selectedOnly: true);
        _stopH264Button.Click += async (_, _) => await StopH264TrialAsync("Stopped by sender.");

        panel.Controls.Add(_startH264AllButton);
        panel.Controls.Add(_startH264SelectedButton);
        panel.Controls.Add(_stopH264Button);
        Controls.Add(panel);
        panel.BringToFront();
    }

    private async Task StartH264TrialAsync(bool selectedOnly)
    {
        if (_isStreamingScreen || _h264Cts is not null)
        {
            AddLog("Stop the current screen stream first.");
            return;
        }

        List<object> targets;
        if (selectedOnly)
        {
            if (_clientList.SelectedItem is not object selected)
            {
                AddLog("Select a client first.");
                return;
            }

            targets = new List<object> { selected };
        }
        else
        {
            targets = GetClientSnapshot().Cast<object>().ToList();
        }

        if (targets.Count == 0)
        {
            AddLog("No connected clients.");
            return;
        }

        string streamId = Guid.NewGuid().ToString("N");
        int fps = Math.Clamp((int)_screenFpsBox.Value, 1, 60);
        int scale = Math.Clamp((int)_screenScaleBox.Value, 25, 100);
        string encoder = Environment.GetEnvironmentVariable("LAN_H264_ENCODER") ?? "libx264";

        var info = new H264StreamInfo
        {
            StreamId = streamId,
            Fps = fps,
            ScalePercent = scale,
            CaptureSource = "gdigrab-primary",
            Encoder = encoder,
        };

        byte[] startPayload = JsonSerializer.SerializeToUtf8Bytes(info);
        foreach (var target in targets.ToList())
        {
            await SendPacketToClientAsync((dynamic)target, PacketType.ScreenH264Start, startPayload, CancellationToken.None);
        }

        _h264Cts = new CancellationTokenSource();
        _h264Streamer = new FfmpegH264Streamer(Environment.GetEnvironmentVariable("LAN_FFMPEG_PATH") ?? "ffmpeg");
        _h264Streamer.Log += message => AddLog("ffmpeg: " + message);

        _isStreamingScreen = true;
        _startH264AllButton!.Enabled = false;
        _startH264SelectedButton!.Enabled = false;
        _stopH264Button!.Enabled = true;
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateScreenStatus($"H.264 trial streaming: {fps}fps / scale {scale}% / {encoder}");

        CancellationToken token = _h264Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _h264Streamer.RunAsync(
                    fps,
                    scale,
                    encoder,
                    async (chunk, ct) =>
                    {
                        foreach (var target in targets.ToList())
                        {
                            await SendPacketToClientAsync((dynamic)target, PacketType.ScreenH264Data, chunk, ct);
                        }
                    },
                    token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog("H.264 trial error: " + ex.Message);
            }
            finally
            {
                await StopH264TrialAsync("H.264 process ended.");
            }
        });
    }

    private async Task StopH264TrialAsync(string reason)
    {
        List<object> targets = GetClientSnapshot().Cast<object>().ToList();
        try { _h264Cts?.Cancel(); } catch { }
        try { _h264Streamer?.Stop(); } catch { }

        var stopInfo = new H264StreamInfo
        {
            StreamId = _activeScreenStreamId ?? "",
            Transport = reason,
        };
        byte[] stopPayload = JsonSerializer.SerializeToUtf8Bytes(stopInfo);
        foreach (var target in targets)
        {
            await SendPacketToClientAsync((dynamic)target, PacketType.ScreenH264Stop, stopPayload, CancellationToken.None);
        }

        try { _h264Cts?.Dispose(); } catch { }
        _h264Cts = null;
        _h264Streamer = null;
        _isStreamingScreen = false;

        if (_startH264AllButton is not null) _startH264AllButton.Enabled = true;
        if (_startH264SelectedButton is not null) _startH264SelectedButton.Enabled = true;
        if (_stopH264Button is not null) _stopH264Button.Enabled = false;
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateScreenStatus("H.264 trial stopped");
    }
}