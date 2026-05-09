using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LanShared.Contracts;

namespace LanSender.ScreenStreaming;

public sealed class ScreenCaptureService
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    public ScreenFrame CaptureVirtualScreenJpeg(string streamId, long frameNo, int jpegQuality)
    {
        return CaptureVirtualScreenJpeg(streamId, frameNo, jpegQuality, 100);
    }

    public ScreenFrame CaptureVirtualScreenJpeg(string streamId, long frameNo, int jpegQuality, int scalePercent)
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        jpegQuality = Math.Clamp(jpegQuality, 20, 95);
        scalePercent = Math.Clamp(scalePercent, 25, 100);

        using var sourceBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);

        using (Graphics graphics = Graphics.FromImage(sourceBitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursorIfAvailable(graphics, bounds);
        }

        int outputWidth = Math.Max(1, bounds.Width * scalePercent / 100);
        int outputHeight = Math.Max(1, bounds.Height * scalePercent / 100);

        using Bitmap outputBitmap = scalePercent == 100
            ? new Bitmap(sourceBitmap)
            : ResizeForStreaming(sourceBitmap, outputWidth, outputHeight);

        using var memory = new MemoryStream();

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
        outputBitmap.Save(memory, JpegCodec, parameters);

        var info = new ScreenFrameInfo
        {
            StreamId = streamId,
            FrameNo = frameNo,
            Width = outputBitmap.Width,
            Height = outputBitmap.Height,
            Quality = jpegQuality,
            Format = "jpeg",
            CapturedAtUtc = DateTime.UtcNow,
        };

        return new ScreenFrame(info, memory.ToArray());
    }

    private static Bitmap ResizeForStreaming(Bitmap source, int width, int height)
    {
        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);

        using Graphics graphics = Graphics.FromImage(resized);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;

        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return resized;
    }

    private static void DrawCursorIfAvailable(Graphics graphics, Rectangle screenBounds)
    {
        CURSORINFO cursorInfo = new()
        {
            cbSize = Marshal.SizeOf<CURSORINFO>(),
        };

        if (!GetCursorInfo(ref cursorInfo))
        {
            return;
        }

        if (cursorInfo.flags != CURSOR_SHOWING || cursorInfo.hCursor == IntPtr.Zero)
        {
            return;
        }

        IntPtr hdc = graphics.GetHdc();

        try
        {
            int x = cursorInfo.ptScreenPos.X - screenBounds.Left;
            int y = cursorInfo.ptScreenPos.Y - screenBounds.Top;
            DrawIcon(hdc, x, y, cursorInfo.hCursor);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);
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
