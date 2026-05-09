namespace LanSender.Contracts;

public sealed class BatchStartInfo
{
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
}

public sealed class FileStartInfo
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool OpenAfterReceive { get; set; }
    public string OpenRequestId { get; set; } = "";
}

public sealed class TransferCancelInfo
{
    public string Reason { get; set; } = "";
}

public sealed class ScreenVideoStartInfo
{
    public string StreamId { get; set; } = "";
    public int Fps { get; set; }
    public int Quality { get; set; }
    public string Format { get; set; } = "jpeg/mjpeg";
    public DateTime StartedAtUtc { get; set; }
}

public sealed class ScreenFrameInfo
{
    public string StreamId { get; set; } = "";
    public long FrameNo { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Quality { get; set; }
    public string Format { get; set; } = "jpeg";
    public DateTime CapturedAtUtc { get; set; }
}

public sealed class ScreenVideoStopInfo
{
    public string StreamId { get; set; } = "";
    public DateTime StoppedAtUtc { get; set; }
    public string Reason { get; set; } = "Stopped by sender.";
}
