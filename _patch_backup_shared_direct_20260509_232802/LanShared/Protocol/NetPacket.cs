using System.Buffers.Binary;
using System.Net.Sockets;

namespace LanShared.Protocol;

public readonly record struct ReceivedPacket(byte Type, byte[] Payload);

public static class NetPacket
{
    private const int HeaderSize = 5;
    private const int MaxPayloadSize = 32 * 1024 * 1024;

    public static async Task WriteAsync(
        NetworkStream stream,
        byte packetType,
        byte[] payload,
        CancellationToken token)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidOperationException($"Payload is too large: {payload.Length:n0} bytes");
        }

        byte[] header = new byte[HeaderSize];
        header[0] = packetType;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header.AsMemory(0, header.Length), token);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload.AsMemory(0, payload.Length), token);
        }

        await stream.FlushAsync(token);
    }

    public static async Task<ReceivedPacket?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize];
        bool headerRead = await ReadExactlyOrEndAsync(stream, header, token);

        if (!headerRead)
        {
            return null;
        }

        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));

        if (payloadLength < 0 || payloadLength > MaxPayloadSize)
        {
            throw new InvalidOperationException($"Invalid payload length: {payloadLength:n0} bytes");
        }

        byte[] payload = new byte[payloadLength];

        if (payloadLength > 0)
        {
            bool payloadRead = await ReadExactlyOrEndAsync(stream, payload, token);

            if (!payloadRead)
            {
                return null;
            }
        }

        return new ReceivedPacket(header[0], payload);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
