using System.Drawing.Imaging;
using LanShared.Contracts;

namespace LanSender.ScreenStreaming;

public sealed class ScreenCaptureService
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    public ScreenFrame CaptureVirtualScreenJpeg(string streamId, long frameNo, int jpegQuality)
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        jpegQuality = Math.Clamp(jpegQuality, 20, 95);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursorIfPossible(graphics, bounds);
        }

        using var memory = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
        bitmap.Save(memory, JpegCodec, parameters);

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

    private static void DrawCursorIfPossible(Graphics graphics, Rectangle screenBounds)
    {
        try
        {
            Point cursorPosition = Cursor.Position;
            Rectangle cursorRectangle = new(
                cursorPosition.X - screenBounds.Left,
                cursorPosition.Y - screenBounds.Top,
                Cursors.Default.Size.Width,
                Cursors.Default.Size.Height);

            Cursors.Default.Draw(graphics, cursorRectangle);
        }
        catch
        {
            // Cursor drawing is optional. Capture must continue even when cursor state cannot be read.
        }
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