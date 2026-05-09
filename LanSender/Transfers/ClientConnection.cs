using System.Net.Sockets;

namespace LanSender.Transfers;

public sealed class ClientConnection : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public TcpClient Client { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    public string DisplayName { get; }

    public ClientConnection(TcpClient client)
    {
        Client = client;
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        DisplayName = $"{endpoint} / {ConnectedAt:HH:mm:ss}";
    }

    public override string ToString() => DisplayName;

    public void Dispose()
    {
        try
        {
            Client.Close();
        }
        catch
        {
        }

        WriteLock.Dispose();
    }
}
