using System.Diagnostics;
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
        return CaptureDesktopJpeg(streamId, frameNo, jpegQuality, 100, ScreenCaptureSource.Virtual);
    }

    public ScreenFrame CaptureVirtualScreenJpeg(string streamId, long frameNo, int jpegQuality, int scalePercent)
    {
        return CaptureDesktopJpeg(streamId, frameNo, jpegQuality, scalePercent, ScreenCaptureSource.Virtual);
    }

    public ScreenFrame CaptureDesktopJpeg(
        string streamId,
        long frameNo,
        int jpegQuality,
        int scalePercent,
        string captureSource)
    {
        Rectangle bounds = ResolveCaptureBounds(captureSource);
        jpegQuality = Math.Clamp(jpegQuality, 20, 95);
        scalePercent = Math.Clamp(scalePercent, 25, 100);

        int outputWidth = Math.Max(1, bounds.Width * scalePercent / 100);
        int outputHeight = Math.Max(1, bounds.Height * scalePercent / 100);

        var totalStopwatch = Stopwatch.StartNew();
        double copyMs;
        double encodeMs;

        using var outputBitmap = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);

        var copyStopwatch = Stopwatch.StartNew();
        CaptureAndScaleDesktop(bounds, outputBitmap);
        DrawCursorIfAvailable(outputBitmap, bounds, scalePercent);
        copyStopwatch.Stop();
        copyMs = copyStopwatch.Elapsed.TotalMilliseconds;

        using var memory = new MemoryStream(Math.Max(64 * 1024, outputWidth * outputHeight / 8));

        var encodeStopwatch = Stopwatch.StartNew();
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            outputBitmap.Save(memory, JpegCodec, parameters);
        }
        encodeStopwatch.Stop();
        encodeMs = encodeStopwatch.Elapsed.TotalMilliseconds;

        totalStopwatch.Stop();

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

        return new ScreenFrame(info, memory.ToArray(), totalStopwatch.Elapsed.TotalMilliseconds, copyMs, encodeMs);
    }

    private static Rectangle ResolveCaptureBounds(string captureSource)
    {
        if (string.Equals(captureSource, ScreenCaptureSource.Primary, StringComparison.OrdinalIgnoreCase))
        {
            return Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        }

        return SystemInformation.VirtualScreen;
    }

    private static void CaptureAndScaleDesktop(Rectangle sourceBounds, Bitmap destination)
    {
        using Graphics graphics = Graphics.FromImage(destination);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;

        IntPtr desktopDc = GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed while capturing desktop.");
        }

        IntPtr targetDc = graphics.GetHdc();

        try
        {
            bool ok;

            if (destination.Width == sourceBounds.Width && destination.Height == sourceBounds.Height)
            {
                ok = BitBlt(
                    targetDc,
                    0,
                    0,
                    destination.Width,
                    destination.Height,
                    desktopDc,
                    sourceBounds.Left,
                    sourceBounds.Top,
                    SRCCOPY | CAPTUREBLT);
            }
            else
            {
                SetStretchBltMode(targetDc, COLORONCOLOR);

                ok = StretchBlt(
                    targetDc,
                    0,
                    0,
                    destination.Width,
                    destination.Height,
                    desktopDc,
                    sourceBounds.Left,
                    sourceBounds.Top,
                    sourceBounds.Width,
                    sourceBounds.Height,
                    SRCCOPY | CAPTUREBLT);
            }

            if (!ok)
            {
                throw new InvalidOperationException($"Desktop copy failed. Win32Error={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            graphics.ReleaseHdc(targetDc);
            ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static void DrawCursorIfAvailable(Bitmap destination, Rectangle screenBounds, int scalePercent)
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

        double scaleX = destination.Width / (double)screenBounds.Width;
        double scaleY = destination.Height / (double)screenBounds.Height;

        int x = (int)Math.Round((cursorInfo.ptScreenPos.X - screenBounds.Left) * scaleX);
        int y = (int)Math.Round((cursorInfo.ptScreenPos.Y - screenBounds.Top) * scaleY);
        int cursorSize = Math.Max(10, 32 * Math.Clamp(scalePercent, 25, 100) / 100);

        using Graphics graphics = Graphics.FromImage(destination);
        IntPtr hdc = graphics.GetHdc();

        try
        {
            DrawIconEx(hdc, x, y, cursorInfo.hCursor, cursorSize, cursorSize, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    private const int COLORONCOLOR = 3;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

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
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int wDest,
        int hDest,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int wSrc,
        int hSrc,
        int rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        int istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        int diFlags);
}

public sealed class ScreenFrame
{
    public ScreenFrameInfo Info { get; }
    public byte[] ImageBytes { get; }
    public double CaptureMs { get; }
    public double CopyMs { get; }
    public double EncodeMs { get; }

    public ScreenFrame(ScreenFrameInfo info, byte[] imageBytes)
        : this(info, imageBytes, 0, 0, 0)
    {
    }

    public ScreenFrame(ScreenFrameInfo info, byte[] imageBytes, double captureMs, double copyMs, double encodeMs)
    {
        Info = info;
        ImageBytes = imageBytes;
        CaptureMs = captureMs;
        CopyMs = copyMs;
        EncodeMs = encodeMs;
    }
}
