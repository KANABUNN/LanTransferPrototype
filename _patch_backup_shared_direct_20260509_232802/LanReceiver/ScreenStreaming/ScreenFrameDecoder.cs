using System.Buffers.Binary;
using System.Text.Json;
using LanReceiver.Contracts;

namespace LanReceiver.ScreenStreaming;

internal sealed record DecodedScreenFrame(ScreenFrameInfo Info, Image Image, int ByteSize);

internal static class ScreenFrameDecoder
{
    public static DecodedScreenFrame Decode(byte[] payload)
    {
        if (payload.Length < 4)
        {
            throw new InvalidDataException("画面フレームのペイロードが短すぎます。");
        }

        int infoLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));

        if (infoLength < 1 || infoLength > payload.Length - 4)
        {
            throw new InvalidDataException("画面フレームのメタデータ長が不正です。");
        }

        ScreenFrameInfo? info = JsonSerializer.Deserialize<ScreenFrameInfo>(payload.AsSpan(4, infoLength));

        if (info is null)
        {
            throw new InvalidDataException("画面フレームのメタデータを解析できません。");
        }

        byte[] imageBytes = payload.AsSpan(4 + infoLength).ToArray();

        using var memoryStream = new MemoryStream(imageBytes);
        using Image loadedImage = Image.FromStream(memoryStream);

        return new DecodedScreenFrame(info, new Bitmap(loadedImage), imageBytes.Length);
    }
}
