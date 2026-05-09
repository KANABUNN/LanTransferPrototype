using System.Net.Sockets;

namespace LanSender.Protocol;

public readonly record struct ReceivedPacket(byte Type, byte[] Payload);

public static class NetPacket
{
    public static async Task WriteAsync(
        NetworkStream stream,
        byte packetType,
        byte[] payload,
        CancellationToken token)
    {
        await LanShared.Protocol.NetPacket.WriteAsync(stream, packetType, payload, token);
    }

    public static async Task<ReceivedPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        LanShared.Protocol.ReceivedPacket? packet = await LanShared.Protocol.NetPacket.ReadAsync(stream, token);

        if (packet is null)
        {
            return null;
        }

        return new ReceivedPacket(packet.Value.Type, packet.Value.Payload);
    }
}
