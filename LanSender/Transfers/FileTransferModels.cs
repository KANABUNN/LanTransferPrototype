namespace LanSender.Transfers;

public sealed class TransferItem
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required long Size { get; init; }

    public override string ToString() => $"{RelativePath} ({FormatBytes(Size)})";

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

public enum TransferResult
{
    Success,
    Canceled,
    Failed,
}
