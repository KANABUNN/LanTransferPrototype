namespace LanShared.Contracts;

public class BatchStartInfo
{
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
}

public class FileStartInfo
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool OpenAfterReceive { get; set; }
    public string OpenRequestId { get; set; } = "";
}

public class TransferCancelInfo
{
    public string Reason { get; set; } = "";
}

public class ScreenVideoStartInfo
{
    public string StreamId { get; set; } = "";
    public int Fps { get; set; }
    public int Quality { get; set; }
    public string Format { get; set; } = "jpeg/mjpeg";
    public DateTime StartedAtUtc { get; set; }
}

public class ScreenFrameInfo
{
    public string StreamId { get; set; } = "";
    public long FrameNo { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Quality { get; set; }
    public string Format { get; set; } = "jpeg";
    public DateTime CapturedAtUtc { get; set; }
}

public class ScreenVideoStopInfo
{
    public string StreamId { get; set; } = "";
    public DateTime StoppedAtUtc { get; set; }
    public string Reason { get; set; } = "Stopped by sender.";
}
