using System.Text.Json;
using LanSender.ScreenStreaming;
using LanShared.Contracts;
using LanShared.Protocol;

namespace LanSender;

public sealed partial class SenderForm
{
    private Button? _startH264AllButton;
    private Button? _startH264SelectedButton;
    private Button? _stopH264Button;
    private NumericUpDown? _h264FpsBox;
    private NumericUpDown? _h264ScaleBox;
    private NumericUpDown? _h264BitrateBox;
    private ComboBox? _h264EncoderBox;
    private Label? _h264StatusLabel;
    private CancellationTokenSource? _h264Cts;
    private FfmpegH264Streamer? _h264Streamer;
    private string? _activeH264StreamId;
    private H264SenderConfig _h264Config = H264SenderConfig.Load();
    private readonly List<object> _activeH264Targets = new();

    private void InitializeH264Feature()
    {
        if (_startH264AllButton is not null)
        {
            return;
        }

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 74,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(8, 4, 8, 4),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        settingsPanel.Controls.Add(new Label { Text = "H.264:", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });

        settingsPanel.Controls.Add(new Label { Text = "FPS", AutoSize = true, Padding = new Padding(0, 7, 4, 0) });
        _h264FpsBox = new NumericUpDown { Minimum = 1, Maximum = 60, Value = _h264Config.Fps, Width = 58 };
        settingsPanel.Controls.Add(_h264FpsBox);

        settingsPanel.Controls.Add(new Label { Text = "Scale", AutoSize = true, Padding = new Padding(8, 7, 4, 0) });
        _h264ScaleBox = new NumericUpDown { Minimum = 25, Maximum = 100, Increment = 5, Value = _h264Config.ScalePercent, Width = 58 };
        settingsPanel.Controls.Add(_h264ScaleBox);

        settingsPanel.Controls.Add(new Label { Text = "Bitrate(k)", AutoSize = true, Padding = new Padding(8, 7, 4, 0) });
        _h264BitrateBox = new NumericUpDown { Minimum = 500, Maximum = 100000, Increment = 500, Value = _h264Config.BitrateKbps, Width = 82 };
        settingsPanel.Controls.Add(_h264BitrateBox);

        settingsPanel.Controls.Add(new Label { Text = "Encoder", AutoSize = true, Padding = new Padding(8, 7, 4, 0) });
        _h264EncoderBox = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
        _h264EncoderBox.Items.AddRange(new object[] { "libx264", "h264_mf", "h264_nvenc", "h264_qsv", "h264_amf" });
        _h264EncoderBox.Text = string.IsNullOrWhiteSpace(_h264Config.Encoder) ? "libx264" : _h264Config.Encoder;
        settingsPanel.Controls.Add(_h264EncoderBox);

        _startH264AllButton = new Button { Text = "H.264 all", Width = 100 };
        _startH264SelectedButton = new Button { Text = "H.264 selected", Width = 120 };
        _stopH264Button = new Button { Text = "Stop H.264", Width = 105, Enabled = false };

        _startH264AllButton.Click += async (_, _) => await StartH264StreamAsync(selectedOnly: false);
        _startH264SelectedButton.Click += async (_, _) => await StartH264StreamAsync(selectedOnly: true);
        _stopH264Button.Click += async (_, _) => await StopH264StreamAsync("Stopped by sender.", notifyReceivers: true);

        settingsPanel.Controls.Add(_startH264AllButton);
        settingsPanel.Controls.Add(_startH264SelectedButton);
        settingsPanel.Controls.Add(_stopH264Button);

        _h264StatusLabel = new Label
        {
            Text = "H.264 ready",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        panel.Controls.Add(settingsPanel, 0, 0);
        panel.Controls.Add(_h264StatusLabel, 0, 1);
        Controls.Add(panel);
        panel.BringToFront();

        FormClosing += (_, _) => StopH264LocalOnly();
    }

    private async Task StartH264StreamAsync(bool selectedOnly)
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

        H264SenderSettings settings = BuildH264SettingsFromUi();
        _h264Config.Apply(settings);
        _h264Config.Save();

        string streamId = Guid.NewGuid().ToString("N");
        _activeH264StreamId = streamId;
        _activeH264Targets.Clear();
        _activeH264Targets.AddRange(targets);

        var info = new H264StreamInfo
        {
            StreamId = streamId,
            Fps = settings.Fps,
            ScalePercent = settings.ScalePercent,
            BitrateKbps = settings.BitrateKbps,
            CaptureSource = "gdigrab-primary",
            Encoder = settings.Encoder,
            Mode = "production",
            LowLatency = true,
            FullScreen = true,
            AlwaysOnTop = true,
        };

        byte[] startPayload = JsonSerializer.SerializeToUtf8Bytes(info);
        foreach (object target in targets.ToList())
        {
            await SendPacketToClientAsync((dynamic)target, PacketType.ScreenH264Start, startPayload, CancellationToken.None);
        }

        _h264Cts = new CancellationTokenSource();
        _h264Streamer = new FfmpegH264Streamer(settings.FfmpegPath);
        _h264Streamer.Log += message => AddLog("ffmpeg: " + message);
        _h264Streamer.StatsChanged += stats => UpdateH264Status($"H.264 streaming: {settings.Fps}fps / scale {settings.ScalePercent}% / {settings.BitrateKbps}k / {settings.Encoder} / {FormatBytes(stats.Bytes)} / {stats.Mbps:0.0} Mbps");

        _isStreamingScreen = true;
        SetH264Buttons(streaming: true);
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateH264Status($"H.264 starting: {settings.Fps}fps / scale {settings.ScalePercent}% / {settings.BitrateKbps}k / {settings.Encoder}");
        UpdateScreenStatus($"H.264 streaming: {settings.Fps}fps / scale {settings.ScalePercent}% / {settings.Encoder}");

        CancellationToken token = _h264Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _h264Streamer.RunAsync(
                    settings,
                    async (chunk, ct) =>
                    {
                        foreach (object target in _activeH264Targets.ToList())
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
                AddLog("H.264 stream error: " + ex.Message);
            }
            finally
            {
                await StopH264StreamAsync("H.264 process ended.", notifyReceivers: true);
            }
        });
    }

    private H264SenderSettings BuildH264SettingsFromUi()
    {
        return new H264SenderSettings
        {
            FfmpegPath = _h264Config.FfmpegPath,
            Fps = (int)(_h264FpsBox?.Value ?? 60),
            ScalePercent = (int)(_h264ScaleBox?.Value ?? 100),
            BitrateKbps = (int)(_h264BitrateBox?.Value ?? 12000),
            Encoder = _h264EncoderBox?.Text ?? "libx264",
            Preset = _h264Config.Preset,
            LogLevel = _h264Config.LogLevel,
            ChunkSizeBytes = _h264Config.ChunkSizeBytes,
        }.Normalize();
    }

    private async Task StopH264StreamAsync(string reason, bool notifyReceivers)
    {
        List<object> targets = _activeH264Targets.Count > 0
            ? _activeH264Targets.ToList()
            : GetClientSnapshot().Cast<object>().ToList();

        try { _h264Cts?.Cancel(); } catch { }
        try { _h264Streamer?.Stop(); } catch { }

        if (notifyReceivers)
        {
            var stopInfo = new H264StreamInfo
            {
                StreamId = _activeH264StreamId ?? "",
                Transport = reason,
                Mode = "production",
            };
            byte[] stopPayload = JsonSerializer.SerializeToUtf8Bytes(stopInfo);
            foreach (object target in targets)
            {
                try
                {
                    await SendPacketToClientAsync((dynamic)target, PacketType.ScreenH264Stop, stopPayload, CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        try { _h264Cts?.Dispose(); } catch { }
        _h264Cts = null;
        _h264Streamer = null;
        _activeH264StreamId = null;
        _activeH264Targets.Clear();
        _isStreamingScreen = false;

        SetH264Buttons(streaming: false);
        SetSendButtonsEnabled(_serverCts is not null);
        UpdateH264Status("H.264 stopped");
        UpdateScreenStatus("H.264 stopped");
    }

    private void StopH264LocalOnly()
    {
        try { _h264Cts?.Cancel(); } catch { }
        try { _h264Streamer?.Stop(); } catch { }
        try { _h264Cts?.Dispose(); } catch { }
        _h264Cts = null;
        _h264Streamer = null;
        _activeH264Targets.Clear();
    }

    private void SetH264Buttons(bool streaming)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetH264Buttons(streaming)));
            return;
        }

        if (_startH264AllButton is not null) _startH264AllButton.Enabled = !streaming;
        if (_startH264SelectedButton is not null) _startH264SelectedButton.Enabled = !streaming;
        if (_stopH264Button is not null) _stopH264Button.Enabled = streaming;
        if (_h264FpsBox is not null) _h264FpsBox.Enabled = !streaming;
        if (_h264ScaleBox is not null) _h264ScaleBox.Enabled = !streaming;
        if (_h264BitrateBox is not null) _h264BitrateBox.Enabled = !streaming;
        if (_h264EncoderBox is not null) _h264EncoderBox.Enabled = !streaming;
    }

    private void UpdateH264Status(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateH264Status(text)));
            return;
        }

        if (_h264StatusLabel is not null)
        {
            _h264StatusLabel.Text = text;
        }
    }
}

public sealed class H264SenderConfig
{
    public string? FfmpegPath { get; set; }
    public string Encoder { get; set; } = "libx264";
    public int Fps { get; set; } = 60;
    public int ScalePercent { get; set; } = 100;
    public int BitrateKbps { get; set; } = 12000;
    public string Preset { get; set; } = "ultrafast";
    public string LogLevel { get; set; } = "warning";
    public int ChunkSizeBytes { get; set; } = 64 * 1024;

    private static string PathName => Path.Combine(AppContext.BaseDirectory, "h264_sender_config.json");

    public static H264SenderConfig Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                H264SenderConfig? loaded = JsonSerializer.Deserialize<H264SenderConfig>(File.ReadAllText(PathName));
                if (loaded is not null)
                {
                    return loaded.Normalize();
                }
            }
        }
        catch
        {
        }

        var config = new H264SenderConfig().Normalize();
        config.Save();
        return config;
    }

    public void Apply(H264SenderSettings settings)
    {
        settings = settings.Normalize();
        FfmpegPath = settings.FfmpegPath;
        Encoder = settings.Encoder;
        Fps = settings.Fps;
        ScalePercent = settings.ScalePercent;
        BitrateKbps = settings.BitrateKbps;
        Preset = settings.Preset;
        LogLevel = settings.LogLevel;
        ChunkSizeBytes = settings.ChunkSizeBytes;
    }

    public H264SenderConfig Normalize()
    {
        Encoder = string.IsNullOrWhiteSpace(Encoder) ? "libx264" : Encoder.Trim();
        Fps = Math.Clamp(Fps, 1, 60);
        ScalePercent = Math.Clamp(ScalePercent, 25, 100);
        BitrateKbps = Math.Clamp(BitrateKbps, 500, 100000);
        Preset = string.IsNullOrWhiteSpace(Preset) ? "ultrafast" : Preset.Trim();
        LogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "warning" : LogLevel.Trim();
        ChunkSizeBytes = Math.Clamp(ChunkSizeBytes, 16 * 1024, 512 * 1024);
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