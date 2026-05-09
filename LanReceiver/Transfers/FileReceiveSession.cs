namespace LanReceiver.Transfers;

internal sealed class FileReceiveSession : IDisposable
{
    public required string FileName { get; init; }
    public required string FinalPath { get; init; }
    public required string TempPath { get; init; }
    public required long FileSize { get; init; }
    public long ReceivedBytes { get; set; }
    public required FileStream Stream { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
    }
}
