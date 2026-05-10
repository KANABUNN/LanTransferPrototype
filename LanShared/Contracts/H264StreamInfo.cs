namespace LanShared.Contracts;

public sealed class H264StreamInfo
{
    public string StreamId { get; set; } = "";
    public string Container { get; set; } = "mpegts";
    public string Codec { get; set; } = "h264";
    public int Fps { get; set; }
    public int ScalePercent { get; set; }
    public int BitrateKbps { get; set; }
    public string CaptureSource { get; set; } = "gdigrab-primary";
    public string Encoder { get; set; } = "libx264";
    public string Transport { get; set; } = "netpacket-mpegts";
    public string Mode { get; set; } = "production";
    public bool LowLatency { get; set; } = true;
    public bool FullScreen { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
}