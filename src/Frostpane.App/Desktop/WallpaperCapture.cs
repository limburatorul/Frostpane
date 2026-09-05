using System.Runtime.InteropServices;
using Frostpane.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Frostpane.Desktop;

/// <summary>A downscaled, blurred snapshot of the whole desktop background.</summary>
/// <param name="Pixels">BGRA rows, tightly packed.</param>
internal sealed record WallpaperFrame(byte[] Pixels, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// Captures the desktop background so panes can show it blurred behind their contents.
///
/// Neither DWM backdrop — the Windows 11 acrylic attribute nor the older accent policy — can see
/// an animated wallpaper drawn by Wallpaper Engine: both sample the static wallpaper bitmap and
/// come out a flat colour. Capturing Progman instead picks up whatever is actually painting the
/// background. Panes are top-level windows, so they are not part of Progman and never appear in
/// their own backdrop.
/// </summary>
internal sealed class WallpaperCapture : IDisposable
{
    /// <summary>
    /// Mip level the frame is read back from: each level halves both dimensions. Level 3 keeps
    /// enough structure for the blur to look like glass rather than a flat wash.
    /// </summary>
    private const int Reduction = 3;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly IntPtr _device;
    private readonly IntPtr _context;

    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private readonly object _sampleLock = new();
    private byte[]? _raw;
    private int _rawWidth, _rawHeight, _sourceWidth, _sourceHeight;

    private IntPtr _mipTexture, _mipView, _staging;
    private int _stagingWidth, _stagingHeight;
    private DateTime _lastFrame = DateTime.MinValue;

    /// <summary>
    /// When a frame was last turned into a usable sample. Deliberately not the arrival time: a
    /// frame that arrives and then fails to process would otherwise look like a healthy capture.
    /// </summary>
    public DateTime LastProcessed { get; private set; } = DateTime.MinValue;

    /// <summary>Why processing last failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Blur radius, in pixels of the reduced image.</summary>
    public int Softness { get; set; } = 3;

    /// <summary>How far the sample is lifted towards white, 0–80.</summary>
    public int Brightness { get; set; } = 12;

    /// <summary>Raised when the captured window goes away, so the capture has to be rebuilt.</summary>
    public event Action? Stopped;
    private bool _disposed;

    public event Action<WallpaperFrame>? FrameReady;

    /// <summary>Throws when capture is unavailable; the caller should then simply skip the blur.</summary>
    public WallpaperCapture(IntPtr desktopWindow)
    {
        Check(D3D11.D3D11CreateDevice(IntPtr.Zero, D3D11.DriverTypeHardware, IntPtr.Zero,
                                      D3D11.CreateDeviceBgraSupport, IntPtr.Zero, 0, D3D11.SdkVersion,
                                      out _device, out _, out _context), "D3D11CreateDevice");

        _item = CreateItemForWindow(desktopWindow);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            CreateWinRtDevice(_device), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);

        _session = _framePool.CreateCaptureSession(_item);
        TrySilenceCaptureChrome(_session);

        _item.Closed += (_, _) => Stopped?.Invoke();
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    private static void TrySilenceCaptureChrome(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = false; } catch (Exception) { /* older build */ }

        IntPtr inspectable = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
        try
        {
            Guid id = typeof(IGraphicsCaptureSession3).GUID;
            if (Marshal.QueryInterface(inspectable, in id, out IntPtr borderApi) != 0) return;
            try
            {
                ((IGraphicsCaptureSession3)Marshal.GetObjectForIUnknown(borderApi)).put_IsBorderRequired(false);
            }
            finally { Marshal.Release(borderApi); }
        }
        finally { Marshal.Release(inspectable); }
    }

    private static IDirect3DDevice CreateWinRtDevice(IntPtr d3dDevice)
    {
        Check(Marshal.QueryInterface(d3dDevice, in D3D11.IID_IDXGIDevice, out IntPtr dxgi), "QI IDXGIDevice");
        try
        {
            Check(D3D11.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr graphics), "CreateDirect3D11Device");
            try { return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(graphics); }
            finally { Marshal.Release(graphics); }
        }
        finally { Marshal.Release(dxgi); }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(D3D11.WindowsCreateString(className, className.Length, out IntPtr classId), "WindowsCreateString");
        try
        {
            Guid interopId = typeof(IGraphicsCaptureItemInterop).GUID;
            Check(D3D11.RoGetActivationFactory(classId, ref interopId, out IntPtr factory), "RoGetActivationFactory");
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                Guid itemId = D3D11.IID_IGraphicsCaptureItem;
                Check(interop.CreateForWindow(window, ref itemId, out IntPtr item), "CreateForWindow");
                try { return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { D3D11.WindowsDeleteString(classId); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null || _disposed) return;

        var now = DateTime.UtcNow;
        if (now - _lastFrame < MinInterval) return;    // a blurred backdrop needs nothing like 60 fps
        _lastFrame = now;

        try
        {
            IntPtr texture = GetTexture(frame.Surface);
            try { Reduce(texture, frame.ContentSize.Width, frame.ContentSize.Height); }
            finally { Marshal.Release(texture); }
        }
        catch (Exception ex)
        {
            // A display change tears down the capture surfaces; the next frame rebuilds them.
            LastError = ex.Message;
            ReleaseTextures();
        }
    }

    private static IntPtr GetTexture(IDirect3DSurface surface)
    {
        IntPtr inspectable = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Check(Marshal.QueryInterface(inspectable, in accessId, out IntPtr access), "QI DxgiInterfaceAccess");
            try
            {
                Check(D3D11.GetDxgiInterface(access, ref D3D11.IID_ID3D11Texture2D, out IntPtr texture),
                      "GetInterface Texture2D");
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(inspectable); }
    }

    /// <summary>
    /// Blurs the last sample again and hands it out.
    ///
    /// Kept separate from capturing because a capture only delivers a frame when something
    /// repaints: on a static wallpaper the softness and brightness settings would otherwise have
    /// no visible effect until the wallpaper happened to change.
    /// </summary>
    public void Publish()
    {
        byte[] pixels;
        int width, height, sourceWidth, sourceHeight;

        lock (_sampleLock)
        {
            if (_raw is null) return;
            pixels = (byte[])_raw.Clone();
            width = _rawWidth;
            height = _rawHeight;
            sourceWidth = _sourceWidth;
            sourceHeight = _sourceHeight;
        }

        Blur(pixels, width, height, Math.Clamp(Softness, 1, 10), Math.Clamp(Brightness, 0, 80));

        LastProcessed = DateTime.UtcNow;
        LastError = null;
        FrameReady?.Invoke(new WallpaperFrame(pixels, width, height, sourceWidth, sourceHeight));
    }

    /// <summary>Mip-maps the frame down to a fraction of its size, then reads that level back.</summary>
    private void Reduce(IntPtr frameTexture, int width, int height)
    {
        int smallWidth = Math.Max(1, width >> Reduction);
        int smallHeight = Math.Max(1, height >> Reduction);

        EnsureTextures(width, height, smallWidth, smallHeight);

        D3D11.CopySubresourceRegion(_context, _mipTexture, 0, frameTexture, 0);
        D3D11.GenerateMips(_context, _mipView);
        D3D11.CopySubresourceRegion(_context, _staging, 0, _mipTexture, Reduction);

        Check(D3D11.Map(_context, _staging, 0, D3D11.MapRead, out var mapped), "Map");
        try
        {
            var raw = new byte[smallWidth * smallHeight * 4];
            for (int row = 0; row < smallHeight; row++)
                Marshal.Copy(mapped.Data + row * (int)mapped.RowPitch, raw, row * smallWidth * 4, smallWidth * 4);

            lock (_sampleLock)
            {
                _raw = raw;
                _rawWidth = smallWidth;
                _rawHeight = smallHeight;
                _sourceWidth = width;
                _sourceHeight = height;
            }
        }
        finally { D3D11.Unmap(_context, _staging, 0); }

        Publish();
    }

    private void EnsureTextures(int width, int height, int smallWidth, int smallHeight)
    {
        if (_staging != IntPtr.Zero && _stagingWidth == smallWidth && _stagingHeight == smallHeight) return;

        ReleaseTextures();

        var mip = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)(Reduction + 1),
            ArraySize = 1,
            Format = D3D11.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1 },
            Usage = D3D11.UsageDefault,
            BindFlags = D3D11.BindShaderResource | D3D11.BindRenderTarget,
            MiscFlags = D3D11.MiscGenerateMips,
        };
        Check(D3D11.CreateTexture2D(_device, ref mip, out _mipTexture), "CreateTexture2D(mip)");
        Check(D3D11.CreateShaderResourceView(_device, _mipTexture, out _mipView), "CreateShaderResourceView");

        var staging = new Texture2DDesc
        {
            Width = (uint)smallWidth,
            Height = (uint)smallHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1 },
            Usage = D3D11.UsageStaging,
            CpuAccessFlags = D3D11.CpuAccessRead,
        };
        Check(D3D11.CreateTexture2D(_device, ref staging, out _staging), "CreateTexture2D(staging)");

        _stagingWidth = smallWidth;
        _stagingHeight = smallHeight;
    }

    /// <summary>
    /// A separable box blur over the already-reduced image. Mip-mapping alone leaves visible
    /// blocks once the result is stretched back up; two cheap passes turn them into frosted glass.
    /// </summary>
    private static void Blur(byte[] pixels, int width, int height, int radius, int brightness)
    {
        var scratch = new byte[pixels.Length];

        BlurPass(pixels, scratch, width, height, radius, horizontal: true);
        BlurPass(scratch, pixels, width, height, radius, horizontal: false);

        // Progman does not write a meaningful alpha channel, so make the sample fully opaque.
        // The lift is proportional to how dark a pixel is, so highlights are left alone.
        double lift = brightness / 100.0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = Lift(pixels[i], lift);
            pixels[i + 1] = Lift(pixels[i + 1], lift);
            pixels[i + 2] = Lift(pixels[i + 2], lift);
            pixels[i + 3] = 255;
        }
    }

    private static byte Lift(byte value, double amount) =>
        (byte)Math.Clamp(value + (255 - value) * amount, 0, 255);

    private static void BlurPass(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int major = horizontal ? height : width;
        int minor = horizontal ? width : height;

        for (int outer = 0; outer < major; outer++)
        {
            for (int inner = 0; inner < minor; inner++)
            {
                int b = 0, g = 0, r = 0, a = 0, taps = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int sample = inner + k;
                    if (sample < 0 || sample >= minor) continue;

                    int index = horizontal ? (outer * width + sample) * 4 : (sample * width + outer) * 4;
                    b += source[index];
                    g += source[index + 1];
                    r += source[index + 2];
                    a += source[index + 3];
                    taps++;
                }

                int destination = horizontal ? (outer * width + inner) * 4 : (inner * width + outer) * 4;
                target[destination] = (byte)(b / taps);
                target[destination + 1] = (byte)(g / taps);
                target[destination + 2] = (byte)(r / taps);
                target[destination + 3] = (byte)(a / taps);
            }
        }
    }

    private void ReleaseTextures()
    {
        if (_mipView != IntPtr.Zero) { Marshal.Release(_mipView); _mipView = IntPtr.Zero; }
        if (_mipTexture != IntPtr.Zero) { Marshal.Release(_mipTexture); _mipTexture = IntPtr.Zero; }
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        _stagingWidth = _stagingHeight = 0;
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new COMException($"{what} failed.", hr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();

        ReleaseTextures();
        if (_context != IntPtr.Zero) Marshal.Release(_context);
        if (_device != IntPtr.Zero) Marshal.Release(_device);
    }
}
