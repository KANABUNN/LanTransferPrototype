using System.Buffers.Binary;
using System.Text.Json;
using LanSender.Contracts;

namespace LanSender.ScreenStreaming;

public static class JpegFrameEncoder
{
    public static byte[] BuildFramePayload(ScreenFrameInfo info, byte[] imageBytes)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(info);
        byte[] payload = new byte[4 + metadata.Length + imageBytes.Length];

        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), metadata.Length);
        metadata.CopyTo(payload.AsSpan(4));
        imageBytes.CopyTo(payload.AsSpan(4 + metadata.Length));

        return payload;
    }
}
