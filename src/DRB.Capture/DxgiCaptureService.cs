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
    private Texture2D? _stagingTexture;
    private int _width;
    private int _height;

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

        // Create a staging texture for CPU read-back.
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
                // No new frame available yet – not an error.
                return null;
            }

            if (result.Code == DXGI_ERROR_ACCESS_LOST)
            {
                throw new DxgiAccessLostException();
            }

            // Any other failure is unexpected.
            result.CheckError();

            // Copy the desktop texture into our staging texture.
            using var desktopTexture = desktopResource!.QueryInterface<Texture2D>();
            _device.ImmediateContext.CopyResource(desktopTexture, _stagingTexture);
        }
        finally
        {
            desktopResource?.Dispose();
            try { _duplication?.ReleaseFrame(); } catch { /* best-effort */ }
        }

        // Map the staging texture and extract raw BGRA → RGBA bytes.
        var dataBox = _device.ImmediateContext.MapSubresource(
            _stagingTexture, 0, MapMode.Read, MapFlags.None);

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

                    for (int x = 0; x < _width; x++)
                    {
                        int srcIdx = x * bytesPerPixel;
                        int dstIdx = dstOffset + x * bytesPerPixel;

                        // BGRA → RGBA swizzle
                        pixels[dstIdx + 0] = srcRow[srcIdx + 2]; // R
                        pixels[dstIdx + 1] = srcRow[srcIdx + 1]; // G
                        pixels[dstIdx + 2] = srcRow[srcIdx + 0]; // B
                        pixels[dstIdx + 3] = srcRow[srcIdx + 3]; // A
                    }
                }
            }

            return new RawFrame(pixels, _width, _height, DateTime.UtcNow.Ticks);
        }
        finally
        {
            _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
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

        _duplication?.Dispose();
        _duplication = null;

        _output1?.Dispose();
        _output1 = null;

        _device?.Dispose();
        _device = null;
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
