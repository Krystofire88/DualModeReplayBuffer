using System.Runtime.InteropServices;
using DRB.Core;
using DRB.Core.Models;
using Microsoft.Extensions.Logging;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace DRB.Capture;

/// <summary>
/// Captures desktop frames via the DXGI Desktop Duplication API (SharpDX).
/// Designed to run on the dedicated Capture thread inside <see cref="CaptureWorker"/>.
/// </summary>
public sealed class DxgiCaptureService : IDisposable
{
    // HRESULT constants for graceful error handling.
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private readonly Config _config;
    private readonly ILogger _logger;

    // D3D / DXGI resources – all nullable because they are (re-)created at runtime.
    private Device? _device;
    private Output1? _output1;
    private OutputDuplication? _duplication;
    private Texture2D? _stagingTexture;     // BGRA8 staging for SDR path
    private Texture2D? _hdrStagingTexture;  // Source-format staging for HDR path
    private int _width;
    private int _height;
    private bool _isHdr;                    // True if desktop is HDR (R16G16B16A16_Float)
    private int _hdrFrameCount;             // Count HDR frames to sample on frame 5
    private byte[]? _lastValidFrame;         // Last valid frame for frame repeat on timeout

    // HDR debug sample positions
    private static readonly (int x, int y)[] HdrSamplePositions = {
        (100, 100), (500, 300), (1000, 500), (1280, 720), (1920, 540),
        (200, 800), (800, 200), (1500, 400), (600, 900), (100, 1200)
    };

    public DxgiCaptureService(Config config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ───────────────────────────── Initialisation ─────────────────────────────

    /// <summary>
    /// Enumerates DXGI adapters, picks the primary output, and creates the
    /// Desktop Duplication session together with a CPU-readable staging texture.
    /// </summary>
    public void Initialize()
    {
        ReleaseResources();

        _logger.LogInformation("Initializing DXGI Desktop Duplication…");

        using var factory = new Factory1();
        Adapter1? adapter = null;
        Output? output = null;

        // Walk adapters and pick the first output (primary monitor).
        for (int i = 0; i < factory.GetAdapterCount1(); i++)
        {
            var candidate = factory.GetAdapter1(i);
            if (candidate.GetOutputCount() > 0)
            {
                adapter = candidate;
                output = candidate.GetOutput(0);
                break;
            }
            candidate.Dispose();
        }

        if (adapter is null || output is null)
            throw new InvalidOperationException("No DXGI adapter with an active output was found.");

        _logger.LogInformation("Using adapter: {Adapter}, output: {Output}",
            adapter.Description.Description, output.Description.DeviceName);

        // Create D3D11 device on the chosen adapter.
        _device = new Device(adapter, DeviceCreationFlags.BgraSupport);

        // Query Output1 for DuplicateOutput support.
        _output1 = output.QueryInterface<Output1>();
        output.Dispose();
        adapter.Dispose();

        _duplication = _output1.DuplicateOutput(_device);

        var bounds = _output1.Description.DesktopBounds;
        _width = bounds.Right - bounds.Left;
        _height = bounds.Bottom - bounds.Top;

        // Detect desktop format from the duplication description.
        Format desktopFormat = _duplication.Description.ModeDescription.Format;
        _isHdr = desktopFormat == Format.R16G16B16A16_Float;
        _logger.LogInformation("Desktop format: {Format}, HDR: {IsHdr}", desktopFormat, _isHdr);

        // Always create a BGRA8 staging texture for SDR output.
        var stagingDesc = new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CpuAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };
        _stagingTexture = new Texture2D(_device, stagingDesc);

        // For HDR, also create a source-format staging texture for CPU read-back.
        if (_isHdr)
        {
            var hdrDesc = new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desktopFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            _hdrStagingTexture = new Texture2D(_device, hdrDesc);
            _logger.LogInformation("HDR RowPitch: {RowPitch}, expected: {Expected}",
                hdrDesc.Width * 8, hdrDesc.Width * 8);
        }

        _logger.LogInformation("Desktop Duplication initialized – {W}×{H}.", _width, _height);
    }

    // ──────────────────────────── Frame Acquisition ───────────────────────────

    /// <summary>
    /// Attempts to acquire the next desktop frame.
    /// Returns <c>null</c> when the frame should be skipped (timeout, rate-limiting).
    /// Throws <see cref="DxgiAccessLostException"/> when the duplication session
    /// must be re-created (desktop switch, UAC, resolution change, etc.).
    /// </summary>
    public RawFrame? TryAcquireFrame()
    {
        if (_duplication is null || _device is null || _stagingTexture is null)
            throw new InvalidOperationException("DxgiCaptureService has not been initialized.");

        Resource? desktopResource = null;
        try
        {
            // Use a short timeout so we can check the cancellation token frequently.
            var result = _duplication.TryAcquireNextFrame(100, out _, out desktopResource);

            if (result.Code == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // No new frame available yet – push last valid frame if available
                if (_lastValidFrame != null)
                {
                    // Clone the array to avoid race conditions
                    byte[] frameCopy = new byte[_lastValidFrame.Length];
                    Array.Copy(_lastValidFrame, frameCopy, _lastValidFrame.Length);
                    return new RawFrame(frameCopy, _width, _height, DateTime.UtcNow.Ticks);
                }
                return null;
            }

            if (result.Code == DXGI_ERROR_ACCESS_LOST)
            {
                throw new DxgiAccessLostException();
            }

            // Any other failure is unexpected.
            result.CheckError();

            using var desktopTexture = desktopResource!.QueryInterface<Texture2D>();

            if (_isHdr && _hdrStagingTexture is not null)
            {
                // HDR path: copy desktop → HDR staging (same format, no mismatch)
                _device.ImmediateContext.CopyResource(desktopTexture, _hdrStagingTexture);
            }
            else
            {
                // SDR path: copy desktop → BGRA8 staging directly
                _device.ImmediateContext.CopyResource(desktopTexture, _stagingTexture);
            }
        }
        finally
        {
            desktopResource?.Dispose();
            try { _duplication?.ReleaseFrame(); } catch { /* best-effort */ }
        }

        if (_isHdr && _hdrStagingTexture is not null)
        {
            // Map the HDR staging texture and tonemap float16 → BGRA8 on CPU.
            return AcquireHdrFrame();
        }
        else
        {
            // Map the BGRA8 staging texture and copy bytes directly.
            return AcquireSdrFrame();
        }
    }

    /// <summary>
    /// Maps the BGRA8 staging texture and returns raw BGRA bytes.
    /// The encoder's ConvertRgbaToNv12 expects BGRA input.
    /// </summary>
    private RawFrame AcquireSdrFrame()
    {
        var dataBox = _device!.ImmediateContext.MapSubresource(
            _stagingTexture!, 0, MapMode.Read, MapFlags.None);

        try
        {
            int rowPitch = dataBox.RowPitch;
            int bytesPerPixel = 4; // B8G8R8A8
            byte[] pixels = new byte[_width * _height * bytesPerPixel];

            unsafe
            {
                byte* srcPtr = (byte*)dataBox.DataPointer;

                for (int y = 0; y < _height; y++)
                {
                    byte* srcRow = srcPtr + y * rowPitch;
                    int dstOffset = y * _width * bytesPerPixel;

                    // Copy BGRA bytes directly — no swizzle needed.
                    // ConvertRgbaToNv12 expects BGRA (reads B at [0], R at [2]).
                    for (int x = 0; x < _width; x++)
                    {
                        int srcIdx = x * bytesPerPixel;
                        int dstIdx = dstOffset + x * bytesPerPixel;

                        pixels[dstIdx + 0] = srcRow[srcIdx + 0]; // B
                        pixels[dstIdx + 1] = srcRow[srcIdx + 1]; // G
                        pixels[dstIdx + 2] = srcRow[srcIdx + 2]; // R
                        pixels[dstIdx + 3] = srcRow[srcIdx + 3]; // A
                    }
                }
            }

            // Store reference to last valid frame
            _lastValidFrame = pixels;

            return new RawFrame(pixels, _width, _height, DateTime.UtcNow.Ticks);
        }
        finally
        {
            _device!.ImmediateContext.UnmapSubresource(_stagingTexture!, 0);
        }
    }

    /// <summary>
    /// Converts IEEE 754 float16 (two bytes) to float32 manually.
    /// </summary>
    private static float HalfToFloat(byte lo, byte hi)
    {
        ushort bits = (ushort)(lo | (hi << 8));
        int sign = (bits >> 15) & 1;
        int exp = (bits >> 10) & 0x1F;
        int mant = bits & 0x3FF;
        float value;
        if (exp == 0) value = mant * MathF.Pow(2, -24);
        else if (exp == 31) value = mant == 0 ? float.PositiveInfinity : float.NaN;
        else value = (1 + mant / 1024f) * MathF.Pow(2, exp - 15);
        return sign == 0 ? value : -value;
    }

    /// <summary>
    /// Tonemaps linear scRGB to sRGB (matches Windows HDR→SDR screenshot behavior).
    /// Scales so SDR white (1.0) maps to output ~0.85.
    /// </summary>
    private static float Tonemap(float linear)
    {
        if (linear <= 0) return 0;
        // Boost exposure slightly and increase saturation via per-channel scaling
        // Scale: SDR white (1.0) → output ~0.85 (slightly darker than full white)
        linear = linear * (255f / 203f) * 0.78f;  // darkening factor
        linear = Math.Min(linear, 1.0f);
        // sRGB gamma
        return linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>
    /// Maps the HDR (R16G16B16A16_Float) staging texture, applies tonemapping,
    /// and returns BGRA8 bytes.
    /// </summary>
    private RawFrame AcquireHdrFrame()
    {
        var dataBox = _device!.ImmediateContext.MapSubresource(
            _hdrStagingTexture!, 0, MapMode.Read, MapFlags.None);

        try
        {
            int rowPitch = dataBox.RowPitch;
            int bytesPerPixelDst = 4; // BGRA8
            byte[] pixels = new byte[_width * _height * bytesPerPixelDst];

            unsafe
            {
                byte* srcPtr = (byte*)dataBox.DataPointer;

                for (int y = 0; y < _height; y++)
                {
                    byte* srcRow = srcPtr + y * rowPitch;
                    int dstRowOffset = y * _width * bytesPerPixelDst;

                    for (int x = 0; x < _width; x++)
                    {
                        // Read float16 channels (scRGB: R, G, B at offsets 0, 2, 4)
                        float r = HalfToFloat(srcRow[x * 8 + 0], srcRow[x * 8 + 1]);
                        float g = HalfToFloat(srcRow[x * 8 + 2], srcRow[x * 8 + 3]);
                        float b = HalfToFloat(srcRow[x * 8 + 4], srcRow[x * 8 + 5]);

                        // Saturation boost in linear light before gamma
                        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        float satBoost = 1.2f;  // 20% saturation increase
                        r = luma + (r - luma) * satBoost;
                        g = luma + (g - luma) * satBoost;
                        b = luma + (b - luma) * satBoost;
                        r = Math.Clamp(r, 0, 1);
                        g = Math.Clamp(g, 0, 1);
                        b = Math.Clamp(b, 0, 1);

                        // Cool shift: slight blue boost, slight red reduction
                        r = r * 0.96f;
                        b = b * 1.04f;
                        r = Math.Clamp(r, 0, 1);
                        b = Math.Clamp(b, 0, 1);

                        // Then apply Tonemap() to each channel
                        float tr = Tonemap(r);
                        float tg = Tonemap(g);
                        float tb = Tonemap(b);
                        byte rb = (byte)(tr * 255f + 0.5f);
                        byte gb = (byte)(tg * 255f + 0.5f);
                        byte bb = (byte)(tb * 255f + 0.5f);

                        // Write as BGRA (ConvertRgbaToNv12 expects BGRA)
                        int dstIdx = dstRowOffset + x * bytesPerPixelDst;
                        pixels[dstIdx + 0] = bb; // B
                        pixels[dstIdx + 1] = gb; // G
                        pixels[dstIdx + 2] = rb; // R
                        pixels[dstIdx + 3] = 255; // A
                    }
                }

                _hdrFrameCount++;
            }

            // Store reference to last valid frame
            _lastValidFrame = pixels;

            return new RawFrame(pixels, _width, _height, DateTime.UtcNow.Ticks);
        }
        finally
        {
            _device!.ImmediateContext.UnmapSubresource(_hdrStagingTexture!, 0);
        }
    }

    // ──────────────────────────── Rate Limiting ───────────────────────────────

    /// <summary>
    /// Returns the minimum interval between frames based on the current
    /// <see cref="CaptureMode"/> in <see cref="Config"/>.
    /// </summary>
    public TimeSpan GetFrameInterval()
    {
        return _config.CaptureMode switch
        {
            CaptureMode.Focus => TimeSpan.FromMilliseconds(1000.0 / 30), // ~33 ms → 30 FPS
            CaptureMode.Context => TimeSpan.FromSeconds(1),               // 1 FPS
            _ => TimeSpan.FromSeconds(1)
        };
    }

    // ──────────────────────────── Cleanup ─────────────────────────────────────

    private void ReleaseResources()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _hdrStagingTexture?.Dispose();
        _hdrStagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _output1?.Dispose();
        _output1 = null;

        _device?.Dispose();
        _device = null;

        _hdrFrameCount = 0;
    }

    public void Dispose() => ReleaseResources();
}

/// <summary>
/// Sentinel exception thrown when DXGI reports ACCESS_LOST, signalling that the
/// duplication session must be re-created (desktop switch, UAC, resolution change).
/// </summary>
public sealed class DxgiAccessLostException : Exception
{
    public DxgiAccessLostException()
        : base("DXGI Desktop Duplication access lost – session must be re-created.") { }
}
