using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LanShared.Contracts;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace LanSender.ScreenStreaming;

public sealed class DxgiDesktopCaptureService : IDisposable
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly Rectangle _bounds;

    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;

    private DxgiDesktopCaptureService(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication,
        Rectangle bounds)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        _bounds = bounds;
    }

    public static DxgiDesktopCaptureService? TryCreatePrimary()
    {
        IDXGIFactory1? factory = null;
        IDXGIAdapter1? selectedAdapter = null;
        IDXGIOutput? selectedOutput = null;
        IDXGIOutput1? output1 = null;

        try
        {
            DXGI.CreateDXGIFactory1<IDXGIFactory1>(out factory).CheckError();
            if (factory is null)
            {
                return null;
            }

            Rectangle primary = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IDXGIAdapter1? adapter = null;

                try
                {
                    factory.EnumAdapters1(adapterIndex, out adapter).CheckError();
                }
                catch
                {
                    break;
                }

                if (adapter is null)
                {
                    break;
                }

                bool adapterSelected = false;

                for (uint outputIndex = 0; ; outputIndex++)
                {
                    IDXGIOutput? output = null;

                    try
                    {
                        adapter.EnumOutputs(outputIndex, out output).CheckError();
                    }
                    catch
                    {
                        break;
                    }

                    if (output is null)
                    {
                        break;
                    }

                    OutputDescription desc = output.Description;
                    Rectangle desktop = new(
                        desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);

                    if (desktop.IntersectsWith(primary) || desktop == primary)
                    {
                        selectedAdapter = adapter;
                        selectedOutput = output;
                        adapterSelected = true;
                        break;
                    }

                    output.Dispose();
                }

                if (adapterSelected)
                {
                    break;
                }

                adapter.Dispose();
            }

            if (selectedAdapter is null || selectedOutput is null)
            {
                return null;
            }

            FeatureLevel[] featureLevels =
            [
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            ];

            D3D11CreateDevice(
                selectedAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out ID3D11Device? device,
                out _,
                out ID3D11DeviceContext? context).CheckError();

            if (device is null || context is null)
            {
                return null;
            }

            output1 = selectedOutput.QueryInterface<IDXGIOutput1>();
            IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);

            OutputDescription selectedDescription = selectedOutput.Description;
            Rectangle bounds = new(
                selectedDescription.DesktopCoordinates.Left,
                selectedDescription.DesktopCoordinates.Top,
                selectedDescription.DesktopCoordinates.Right - selectedDescription.DesktopCoordinates.Left,
                selectedDescription.DesktopCoordinates.Bottom - selectedDescription.DesktopCoordinates.Top);

            return new DxgiDesktopCaptureService(device, context, duplication, bounds);
        }
        catch
        {
            return null;
        }
        finally
        {
            output1?.Dispose();
            selectedOutput?.Dispose();
            selectedAdapter?.Dispose();
            factory?.Dispose();
        }
    }

    public ScreenFrame? TryCaptureDesktopJpeg(
        string streamId,
        long frameNo,
        int jpegQuality,
        int scalePercent)
    {
        jpegQuality = Math.Clamp(jpegQuality, 20, 95);
        scalePercent = Math.Clamp(scalePercent, 25, 100);

        var totalStopwatch = Stopwatch.StartNew();
        IDXGIResource? resource = null;
        bool frameAcquired = false;

        try
        {
            try
            {
                _duplication.AcquireNextFrame(0, out _, out resource).CheckError();
                frameAcquired = true;
            }
            catch
            {
                return null;
            }

            if (resource is null)
            {
                return null;
            }

            using ID3D11Texture2D desktopTexture = resource.QueryInterface<ID3D11Texture2D>();
            Texture2DDescription desktopDesc = desktopTexture.Description;

            ID3D11Texture2D staging = EnsureStagingTexture(desktopDesc);
            _context.CopyResource(staging, desktopTexture);

            var copyStopwatch = Stopwatch.StartNew();

            int textureWidth = checked((int)desktopDesc.Width);
            int textureHeight = checked((int)desktopDesc.Height);
            using Bitmap fullBitmap = CopyTextureToBitmap(staging, textureWidth, textureHeight);

            Bitmap outputBitmap;

            if (scalePercent == 100)
            {
                outputBitmap = new Bitmap(fullBitmap);
            }
            else
            {
                int outputWidth = Math.Max(1, fullBitmap.Width * scalePercent / 100);
                int outputHeight = Math.Max(1, fullBitmap.Height * scalePercent / 100);

                outputBitmap = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
                using Graphics graphics = Graphics.FromImage(outputBitmap);
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                graphics.DrawImage(fullBitmap, new Rectangle(0, 0, outputWidth, outputHeight));
            }

            DrawCursorIfAvailable(outputBitmap, _bounds, scalePercent);
            copyStopwatch.Stop();

            using (outputBitmap)
            using (var memory = new MemoryStream(Math.Max(64 * 1024, outputBitmap.Width * outputBitmap.Height / 8)))
            {
                var encodeStopwatch = Stopwatch.StartNew();
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
                    outputBitmap.Save(memory, JpegCodec, parameters);
                }
                encodeStopwatch.Stop();
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

                return new ScreenFrame(
                    info,
                    memory.ToArray(),
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    copyStopwatch.Elapsed.TotalMilliseconds,
                    encodeStopwatch.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            resource?.Dispose();

            if (frameAcquired)
            {
                try
                {
                    _duplication.ReleaseFrame();
                }
                catch
                {
                }
            }
        }
    }

    private ID3D11Texture2D EnsureStagingTexture(Texture2DDescription sourceDescription)
    {
        int sourceWidth = checked((int)sourceDescription.Width);
        int sourceHeight = checked((int)sourceDescription.Height);

        if (_stagingTexture is not null &&
            _stagingWidth == sourceWidth &&
            _stagingHeight == sourceHeight)
        {
            return _stagingTexture;
        }

        _stagingTexture?.Dispose();

        var stagingDescription = new Texture2DDescription
        {
            Width = sourceDescription.Width,
            Height = sourceDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDescription.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        _stagingTexture = _device.CreateTexture2D(stagingDescription);
        _stagingWidth = sourceWidth;
        _stagingHeight = sourceHeight;

        return _stagingTexture;
    }

    private unsafe Bitmap CopyTextureToBitmap(ID3D11Texture2D staging, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            int rowBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                byte* src = (byte*)mapped.DataPointer + y * mapped.RowPitch;
                byte* dst = (byte*)bitmapData.Scan0 + y * bitmapData.Stride;
                Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
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

    public void Dispose()
    {
        _stagingTexture?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

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