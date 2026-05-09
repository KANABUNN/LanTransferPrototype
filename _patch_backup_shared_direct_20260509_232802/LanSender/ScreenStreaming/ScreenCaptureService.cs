using System.Drawing.Imaging;
using LanSender.Contracts;

namespace LanSender.ScreenStreaming;

public sealed class ScreenCaptureService
{
    public ScreenFrame CaptureVirtualScreenJpeg(string streamId, long frameNo, int jpegQuality)
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        jpegQuality = Math.Clamp(jpegQuality, 20, 95);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var memory = new MemoryStream();
        ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
        bitmap.Save(memory, jpegCodec, parameters);

        var info = new ScreenFrameInfo
        {
            StreamId = streamId,
            FrameNo = frameNo,
            Width = bounds.Width,
            Height = bounds.Height,
            Quality = jpegQuality,
            Format = "jpeg",
            CapturedAtUtc = DateTime.UtcNow,
        };

        return new ScreenFrame(info, memory.ToArray());
    }
}

public sealed class ScreenFrame
{
    public ScreenFrameInfo Info { get; }
    public byte[] ImageBytes { get; }

    public ScreenFrame(ScreenFrameInfo info, byte[] imageBytes)
    {
        Info = info;
        ImageBytes = imageBytes;
    }
}
