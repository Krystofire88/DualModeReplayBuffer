using System.Runtime.InteropServices;
using System.Text;
using DRB.Core;
using DRB.Core.Models;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

/// <summary>
/// H.264 encoder using Windows Media Foundation (MF) via P/Invoke.
/// Auto-detects GPU encoders (NVENC → QuickSync → AMF → software x264 fallback).
/// Writes fixed-duration MP4 segments to the focus buffer directory.
/// </summary>
public sealed class HardwareVideoEncoder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _segmentDurationSeconds;
    private readonly string _outputDirectory;
    private readonly ILogger _logger;

    // MF COM pointers (IntPtr for vtable dispatch)
    private IntPtr _sinkWriter;
    private int _videoStreamIndex;
    private bool _isWriting;
    private bool _encoderFailed;

    // Segment tracking
    private DateTime _segmentStartTime;
    private long _frameCount;
    private string? _currentSegmentPath;

    // NV12 conversion buffer (reused across frames)
    private byte[]? _nv12Buffer;

    /// <summary>Raised when a segment file has been finalized and closed.</summary>
    public event Action<VideoSegment>? OnSegmentComplete;

    /// <summary>True if the encoder has encountered an unrecoverable error.</summary>
    public bool EncoderFailed => _encoderFailed;

    public HardwareVideoEncoder(Config config, ILogger logger)
    {
        _width = config.EncodeWidth;
        _height = config.EncodeHeight;
        _fps = config.EncodeFps;
        _segmentDurationSeconds = config.SegmentDurationSeconds;
        _outputDirectory = AppPaths.FocusBufferFolder;
        _logger = logger;

        Directory.CreateDirectory(_outputDirectory);

        MfStartup();
        DetectAndLogEncoder();
    }

    // ──────────────────────── Public API ──────────────────────────

    /// <summary>
    /// Push a raw RGBA frame into the encoder. Converts to NV12 and writes to the
    /// current MP4 segment. Automatically rolls to a new segment when the duration
    /// threshold is reached.
    /// </summary>
    public void PushFrame(byte[] rgba, long timestampHns)
    {
        if (_encoderFailed || rgba is null || rgba.Length < _width * _height * 4)
            return;

        // Start a new segment if needed.
        if (!_isWriting)
            BeginSegment();

        // Convert RGBA → NV12.
        var nv12 = ConvertRgbaToNv12(rgba);

        // Write the NV12 sample to the sink writer.
        WriteSample(nv12, timestampHns);

        _frameCount++;

        // Check if we should roll to a new segment.
        long framesPerSegment = (long)_fps * _segmentDurationSeconds;
        if (_frameCount >= framesPerSegment)
        {
            Flush();
        }
    }

    /// <summary>Finalize the current segment and raise <see cref="OnSegmentComplete"/>.</summary>
    public void Flush()
    {
        if (!_isWriting)
            return;

        FinalizeSegment();
    }

    // ──────────────────── MF Startup / Shutdown ──────────────────

    private static void MfStartup()
    {
        int hr = NativeMethods.MFStartup(NativeMethods.MF_VERSION, NativeMethods.MFSTARTUP_FULL);
        Marshal.ThrowExceptionForHR(hr);
    }

    private static void MfShutdown()
    {
        NativeMethods.MFShutdown();
    }

    // ──────────────────── Encoder Detection ──────────────────────

    private void DetectAndLogEncoder()
    {
        string encoderName = DetectBestEncoder();
        _logger.LogInformation("Selected H.264 encoder: {Encoder}", encoderName);
    }

    /// <summary>
    /// Enumerates MFT H.264 encoders and picks the best GPU-accelerated one.
    /// Falls back to the software encoder if no GPU encoder is found.
    /// Only considers MFTs whose friendly name contains H.264-related keywords
    /// to avoid picking up unrelated encoders (e.g. HEIF image extensions).
    /// </summary>
    private string DetectBestEncoder()
    {
        static bool IsH264Name(string name) =>
            name.Contains("H264", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("H.264", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AVC", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("x264", StringComparison.OrdinalIgnoreCase);

        // Enumerate all video encoder MFTs (hardware first).
        int hr = NativeMethods.MFTEnumEx(
            NativeMethods.MFT_CATEGORY_VIDEO_ENCODER,
            NativeMethods.MFT_ENUM_FLAG_HARDWARE | NativeMethods.MFT_ENUM_FLAG_SORTANDFILTER,
            IntPtr.Zero,  // input type: any
            IntPtr.Zero,  // output type: any
            out IntPtr activateArray,
            out int count);

        if (hr < 0 || count == 0)
        {
            // Try again including software encoders.
            hr = NativeMethods.MFTEnumEx(
                NativeMethods.MFT_CATEGORY_VIDEO_ENCODER,
                NativeMethods.MFT_ENUM_FLAG_SYNCMFT | NativeMethods.MFT_ENUM_FLAG_ASYNCMFT |
                NativeMethods.MFT_ENUM_FLAG_SORTANDFILTER,
                IntPtr.Zero,
                IntPtr.Zero,
                out activateArray,
                out count);

            if (hr < 0 || count == 0)
                return "Software (H264 MFT fallback)";
        }

        try
        {
            // Priority order: NVIDIA, Intel, AMD, then first H.264-named MFT.
            string? nvidia = null, intel = null, amd = null, firstH264 = null;

            for (int i = 0; i < count; i++)
            {
                IntPtr activatePtr = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                string? friendlyName = GetMftFriendlyName(activatePtr);

                if (friendlyName is null) continue;

                // Skip MFTs that are not H.264 encoders (e.g. HEIF, VP9, etc.)
                if (!IsH264Name(friendlyName)) continue;

                firstH264 ??= friendlyName;

                if (friendlyName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    nvidia ??= friendlyName;
                else if (friendlyName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    intel ??= friendlyName;
                else if (friendlyName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                    amd ??= friendlyName;
            }

            return nvidia ?? intel ?? amd ?? firstH264 ?? "Software (H264 MFT fallback)";
        }
        finally
        {
            // Release the activate objects.
            for (int i = 0; i < count; i++)
            {
                IntPtr activatePtr = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                if (activatePtr != IntPtr.Zero)
                    Marshal.Release(activatePtr);
            }
            Marshal.FreeCoTaskMem(activateArray);
        }
    }

    private static string? GetMftFriendlyName(IntPtr activate)
    {
        if (activate == IntPtr.Zero) return null;

        // Use the COM interface directly
        var attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(activate);
        
        try
        {
            int hr = attributes.GetAllocatedString(ref NativeMethods.MFT_FRIENDLY_NAME_Attribute, out string? name, out _);
            if (hr >= 0 && name != null)
                return name;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ──────────────────── Segment Management ─────────────────────

    private void BeginSegment()
    {
        _segmentStartTime = DateTime.UtcNow;
        _currentSegmentPath = Path.Combine(
            _outputDirectory,
            _segmentStartTime.ToString("yyyyMMdd_HHmmss_fff") + ".mp4");

        _frameCount = 0;

        try
        {
            CreateSinkWriter(_currentSegmentPath);
            _isWriting = true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to create sink writer for segment: {Path}", _currentSegmentPath);
            _encoderFailed = true;
            _isWriting = false;
            _currentSegmentPath = null;
            return;
        }

        _logger.LogDebug("Started new segment: {Path}", _currentSegmentPath);
    }

    private void FinalizeSegment()
    {
        if (_sinkWriter != IntPtr.Zero)
        {
            try
            {
                // IMFSinkWriter::DoFinalize = vtable slot 11
                int hr = SWDoFinalize(_sinkWriter);
                if (hr < 0)
                    _logger.LogWarning("DoFinalize returned HRESULT: {Hr}", hr);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error finalizing sink writer.");
            }
            finally
            {
                // Release COM object immediately to unlock the file
                Marshal.Release(_sinkWriter);
                _sinkWriter = IntPtr.Zero;
            }
        }

        _isWriting = false;

        if (_currentSegmentPath is not null)
        {
            var duration = TimeSpan.FromSeconds((double)_frameCount / _fps);
            var segment = new VideoSegment(_currentSegmentPath, _segmentStartTime, duration);

            _logger.LogDebug("Completed segment: {Path} ({Duration})", _currentSegmentPath, duration);

            OnSegmentComplete?.Invoke(segment);
            _currentSegmentPath = null;
        }
    }

    // ──────────────────── Sink Writer Setup ──────────────────────

    private void CreateSinkWriter(string outputPath)
    {
        _logger.LogDebug("Creating sink writer for: {Path}", outputPath);

        // Create attributes for the sink writer.
        int hr = NativeMethods.MFCreateAttributes(out IMFAttributes attributes, 4);
        Marshal.ThrowExceptionForHR(hr);

        IntPtr pAttributes = IntPtr.Zero;
        try
        {
            // Enable hardware transforms.
            attributes.SetUINT32(ref NativeMethods.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);

            // Get IntPtr for attributes to pass to MFCreateSinkWriterFromURL
            pAttributes = Marshal.GetComInterfaceForObject(attributes, typeof(IMFAttributes));

            // Create the sink writer for the output file.
            _logger.LogDebug("Calling MFCreateSinkWriterFromURL...");
            hr = NativeMethods.MFCreateSinkWriterFromURL(
                outputPath,
                IntPtr.Zero, // IMFByteStream - pass null
                pAttributes,
                out IntPtr sinkWriter);
            
            if (hr < 0)
            {
                _logger.LogError("MFCreateSinkWriterFromURL failed with HRESULT: {Hr}", hr);
                _encoderFailed = true;
                Marshal.ThrowExceptionForHR(hr);
            }
            
            _sinkWriter = sinkWriter;
            _logger.LogDebug("Sink writer created successfully.");
        }
        finally
        {
            if (pAttributes != IntPtr.Zero)
                Marshal.Release(pAttributes);
            // Release attributes - sink writer keeps its own reference
            Marshal.ReleaseComObject(attributes);
        }

        // Configure the output media type (H.264).
        hr = NativeMethods.MFCreateMediaType(out IMFMediaType outputType);
        Marshal.ThrowExceptionForHR(hr);

        IntPtr pOutputType = IntPtr.Zero;
        try
        {
            SetMediaTypeAttributes(outputType, isOutput: true);

            pOutputType = Marshal.GetComInterfaceForObject(outputType, typeof(IMFMediaType));
            hr = SWAddStream(_sinkWriter, pOutputType, out _videoStreamIndex);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            if (pOutputType != IntPtr.Zero)
                Marshal.Release(pOutputType);
            Marshal.ReleaseComObject(outputType);
        }

        // Configure the input media type (NV12).
        hr = NativeMethods.MFCreateMediaType(out IMFMediaType inputType);
        Marshal.ThrowExceptionForHR(hr);

        IntPtr pInputType = IntPtr.Zero;
        try
        {
            SetMediaTypeAttributes(inputType, isOutput: false);

            pInputType = Marshal.GetComInterfaceForObject(inputType, typeof(IMFMediaType));
            hr = SWSetInputMediaType(_sinkWriter, _videoStreamIndex, pInputType, IntPtr.Zero);
            if (hr < 0)
            {
                _logger.LogError("SetInputMediaType failed with HRESULT: {Hr}", hr);
                _encoderFailed = true;
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            if (pInputType != IntPtr.Zero)
                Marshal.Release(pInputType);
            Marshal.ReleaseComObject(inputType);
        }

        // Begin writing.
        hr = SWBeginWriting(_sinkWriter);
        if (hr < 0)
        {
            _logger.LogError("BeginWriting failed with HRESULT: {Hr}", hr);
            _encoderFailed = true;
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private void SetMediaTypeAttributes(IMFMediaType mediaType, bool isOutput)
    {
        // Major type: Video
        mediaType.SetGUID(ref NativeMethods.MF_MT_MAJOR_TYPE, ref NativeMethods.MFMediaType_Video);

        if (isOutput)
        {
            // Subtype: H264
            mediaType.SetGUID(ref NativeMethods.MF_MT_SUBTYPE, ref NativeMethods.MFVideoFormat_H264);

            // Average bitrate: 8 Mbps
            mediaType.SetUINT32(ref NativeMethods.MF_MT_AVG_BITRATE, 8_000_000);
        }
        else
        {
            // Subtype: NV12
            mediaType.SetGUID(ref NativeMethods.MF_MT_SUBTYPE, ref NativeMethods.MFVideoFormat_NV12);
        }

        // Frame size
        long frameSize = ((long)_width << 32) | (uint)_height;
        mediaType.SetUINT64(ref NativeMethods.MF_MT_FRAME_SIZE, frameSize);

        // Frame rate
        long frameRate = ((long)_fps << 32) | 1;
        mediaType.SetUINT64(ref NativeMethods.MF_MT_FRAME_RATE, frameRate);

        // Pixel aspect ratio: 1:1
        long par = ((long)1 << 32) | 1;
        mediaType.SetUINT64(ref NativeMethods.MF_MT_PIXEL_ASPECT_RATIO, par);

        // Interlace mode: progressive
        mediaType.SetUINT32(ref NativeMethods.MF_MT_INTERLACE_MODE, 2); // MFVideoInterlace_Progressive
    }

    // ──────────────────── Frame Writing ──────────────────────────

    private void WriteSample(byte[] nv12Data, long timestampHns)
    {
        if (_sinkWriter == IntPtr.Zero) return;

        try
        {
            int nv12Size = nv12Data.Length;

            // Create a media buffer.
            int hr = NativeMethods.MFCreateMemoryBuffer(nv12Size, out IntPtr buffer);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // Lock the buffer and copy NV12 data.
                hr = MBLock(buffer, out IntPtr pbBuffer, out _, out _);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    Marshal.Copy(nv12Data, 0, pbBuffer, nv12Size);
                }
                finally
                {
                    MBUnlock(buffer);
                }

                MBSetCurrentLength(buffer, nv12Size);

                // Create a sample and attach the buffer.
                hr = NativeMethods.MFCreateSample(out IntPtr sample);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    SampleAddBuffer(sample, buffer);

                    // Set the timestamp (100-nanosecond units).
                    long sampleTime = _frameCount * (10_000_000L / _fps);
                    SampleSetSampleTime(sample, sampleTime);

                    // Set the duration.
                    long sampleDuration = 10_000_000L / _fps;
                    SampleSetSampleDuration(sample, sampleDuration);

                    // Write the sample.
                    hr = SWWriteSample(_sinkWriter, _videoStreamIndex, sample);
                    Marshal.ThrowExceptionForHR(hr);
                }
                finally
                {
                    Marshal.Release(sample);
                }
            }
            finally
            {
                Marshal.Release(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteSample failed at frameCount={F}", _frameCount);
            _encoderFailed = true;
        }
    }

    // ──────────────────── RGBA → NV12 Conversion ─────────────────

    /// <summary>
    /// Converts RGBA (8-bit per channel, 4 bytes/pixel) to NV12 (planar Y + interleaved UV).
    /// NV12 size = width * height * 3 / 2.
    /// </summary>
    private byte[] ConvertRgbaToNv12(byte[] rgba)
    {
        int ySize = _width * _height;
        int uvSize = ySize / 2;
        int nv12Size = ySize + uvSize;

        _nv12Buffer ??= new byte[nv12Size];

        // Y plane
        for (int y = 0; y < _height; y++)
        {
            int rowOffset = y * _width * 4;
            int yRowOffset = y * _width;

            for (int x = 0; x < _width; x++)
            {
                int srcIdx = rowOffset + x * 4;
                // DXGI gives BGRA, not RGBA - swap R and B
                byte b = rgba[srcIdx + 0];
                byte g = rgba[srcIdx + 1];
                byte r = rgba[srcIdx + 2];

                // BT.601 luma
                _nv12Buffer[yRowOffset + x] = ClampByte((66 * r + 129 * g + 25 * b + 128) / 256 + 16);
            }
        }

        // UV plane (subsampled 2×2)
        int uvOffset = ySize;
        for (int y = 0; y < _height; y += 2)
        {
            int rowOffset = y * _width * 4;
            int uvRowOffset = uvOffset + (y / 2) * _width;

            for (int x = 0; x < _width; x += 2)
            {
                int srcIdx = rowOffset + x * 4;
                // DXGI gives BGRA, not RGBA - swap R and B
                byte b = rgba[srcIdx + 0];
                byte g = rgba[srcIdx + 1];
                byte r = rgba[srcIdx + 2];

                // BT.601 chroma
                byte u = ClampByte((-38 * r - 74 * g + 112 * b + 128) / 256 + 128);
                byte v = ClampByte((112 * r - 94 * g - 18 * b + 128) / 256 + 128);

                _nv12Buffer[uvRowOffset + x] = u;
                _nv12Buffer[uvRowOffset + x + 1] = v;
            }
        }

        return _nv12Buffer;
    }

    private static byte ClampByte(int value) =>
        (byte)Math.Clamp(value, 0, 255);

    // ──────────────────── Dispose ────────────────────────────────

    public void Dispose()
    {
        if (_isWriting)
        {
            try { Flush(); } catch { /* best-effort */ }
        }

        if (_sinkWriter != IntPtr.Zero)
        {
            try { Marshal.Release(_sinkWriter); } catch { }
            _sinkWriter = IntPtr.Zero;
        }

        MfShutdown();
    }

    // ──────────────────── Vtable Helpers (unsafe) ────────────────
    // These use C# 9 function pointers for direct COM vtable dispatch,
    // bypassing .NET's COM interop QueryInterface which fails for
    // IMFMediaBuffer, IMFSample, and IMFSinkWriter.

    // ── IMFMediaBuffer vtable ──
    // IUnknown(0,1,2), Lock(3), Unlock(4), GetCurrentLength(5), SetCurrentLength(6), GetMaxLength(7)

    private static unsafe int MBLock(IntPtr p, out IntPtr ppbBuffer, out int pcbMax, out int pcbCurrent)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, out int, out int, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 3 * IntPtr.Size);
        return fn(p, out ppbBuffer, out pcbMax, out pcbCurrent);
    }

    private static unsafe int MBUnlock(IntPtr p)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 4 * IntPtr.Size);
        return fn(p);
    }

    private static unsafe int MBSetCurrentLength(IntPtr p, int len)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 6 * IntPtr.Size);
        return fn(p, len);
    }

    // ── IMFSample vtable ──
    // IUnknown: QueryInterface(0), AddRef(1), Release(2)
    // IMFAttributes: GetItem(3), GetItemType(4), CompareItem(5), Compare(6),
    //   GetUINT32(7), GetUINT64(8), GetDouble(9), GetGUID(10), GetStringLength(11),
    //   GetString(12), GetAllocatedString(13), GetBlobSize(14), GetBlob(15),
    //   GetAllocatedBlob(16), GetUnknown(17), SetItem(18), DeleteItem(19),
    //   DeleteAllItems(20), SetUINT32(21), SetUINT64(22), SetDouble(23), SetGUID(24),
    //   SetString(25), SetBlob(26), SetUnknown(27), LockStore(28), UnlockStore(29),
    //   GetCount(30), GetItemByIndex(31), CopyAllItems(32)
    // IMFSample: GetSampleFlags(33), SetSampleFlags(34), GetSampleTime(35),
    //   SetSampleTime(36), GetSampleDuration(37), SetSampleDuration(38),
    //   GetBufferCount(39), GetBufferByIndex(40), ConvertToContiguousBuffer(41),
    //   AddBuffer(42)

    private static unsafe int SampleSetSampleTime(IntPtr p, long t)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 36 * IntPtr.Size);
        return fn(p, t);
    }

    private static unsafe int SampleSetSampleDuration(IntPtr p, long d)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 38 * IntPtr.Size);
        return fn(p, d);
    }

    private static unsafe int SampleAddBuffer(IntPtr p, IntPtr buffer)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), 42 * IntPtr.Size);
        return fn(p, buffer);
    }

    // ── IMFSinkWriter vtable ──
    // IUnknown(0,1,2), AddStream(3), SetInputMediaType(4), BeginWriting(5),
    // WriteSample(6), SendStreamTick(7), PlaceMarker(8), NotifyEndOfSegment(9),
    // Flush(10), DoFinalize(11)

    private static unsafe int SWAddStream(IntPtr sw, IntPtr pTargetMediaType, out int pdwStreamIndex)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out int, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(sw), 3 * IntPtr.Size);
        return fn(sw, pTargetMediaType, out pdwStreamIndex);
    }

    private static unsafe int SWSetInputMediaType(IntPtr sw, int dwStreamIndex, IntPtr pInputMediaType, IntPtr pEncodingParameters)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(sw), 4 * IntPtr.Size);
        return fn(sw, dwStreamIndex, pInputMediaType, pEncodingParameters);
    }

    private static unsafe int SWBeginWriting(IntPtr sw)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(sw), 5 * IntPtr.Size);
        return fn(sw);
    }

    private static unsafe int SWWriteSample(IntPtr sw, int dwStreamIndex, IntPtr pSample)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(sw), 6 * IntPtr.Size);
        return fn(sw, dwStreamIndex, pSample);
    }

    private static unsafe int SWDoFinalize(IntPtr sw)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(sw), 11 * IntPtr.Size);
        return fn(sw);
    }

    // ──────────────────── COM Interfaces ─────────────────────────
    // Only IMFAttributes and IMFMediaType are kept as [ComImport] interfaces.
    // IMFMediaBuffer, IMFSample, and IMFSinkWriter use IntPtr + vtable dispatch above.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        int GetItem(ref Guid guidKey, IntPtr pValue);
        int GetItemType(ref Guid guidKey, out int pType);
        int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
        int GetUINT32(ref Guid guidKey, out int punValue);
        int GetUINT64(ref Guid guidKey, out long punValue);
        int GetDouble(ref Guid guidKey, out double pfValue);
        int GetGUID(ref Guid guidKey, out Guid pguidValue);
        int GetStringLength(ref Guid guidKey, out int pcchLength);
        int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, int cchBufSize, out int pcchLength);
        int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        int GetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        int SetItem(ref Guid guidKey, IntPtr Value);
        int DeleteItem(ref Guid guidKey);
        int DeleteAllItems();
        int SetUINT32(ref Guid guidKey, int unValue);
        int SetUINT64(ref Guid guidKey, long unValue);
        int SetDouble(ref Guid guidKey, double fValue);
        int SetGUID(ref Guid guidKey, ref Guid guidValue);
        int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        int SetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize);
        int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int pcItems);
        int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out int pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
        new int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
        new int GetUINT32(ref Guid guidKey, out int punValue);
        new int GetUINT64(ref Guid guidKey, out long punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out int pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, int cchBufSize, out int pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        new int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        new int GetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        new int SetItem(ref Guid guidKey, IntPtr Value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, int unValue);
        new int SetUINT64(ref Guid guidKey, long unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, byte[] pBuf, int cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out int pcItems);
        new int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);
        // IMFMediaType methods
        int GetMajorType(out Guid pguidMajorType);
        int IsCompressedFormat(out bool pfCompressed);
        int IsEqual(IMFMediaType pIMediaType, out int pdwFlags);
        int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MF_SINK_WRITER_STATISTICS
    {
        public long cb;
        public long cVideoStreamCount;
        public long cVideoBytesProcessed;
        public long cVideoSampleCount;
        public long cVideoFrameCount;
        public long cAudioStreamCount;
        public long cAudioBytesProcessed;
        public long cAudioSampleCount;
        public long cAudioFrameCount;
        public long qwFirstSampleTime;
        public long qwLastSampleTime;
        public long dwNumSkippedSamples;
        public long dwNumCorruptedSamples;
        public long dwTotalFrameDrops;
    }

    // ──────────────────── P/Invoke Declarations ──────────────────

    private static class NativeMethods
    {
        public const int MF_VERSION = 0x00020070; // MF 2.0
        public const int MFSTARTUP_FULL = 0;

        // MFT enum flags
        public const int MFT_ENUM_FLAG_HARDWARE = 0x00000010;
        public const int MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
        public const int MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
        public const int MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

        // GUIDs
        public static Guid MFT_CATEGORY_VIDEO_ENCODER =
            new("f79eac7d-e545-4387-bdee-d647d7bde42a");

        public static Guid MFT_FRIENDLY_NAME_Attribute =
            new("314ffbae-5b41-4c95-9c19-4e7d586face3");

        public static Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS =
            new("a634a91c-822b-41b9-a494-4de4643612b0");

        public static Guid MF_MT_MAJOR_TYPE =
            new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

        public static Guid MF_MT_SUBTYPE =
            new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

        public static Guid MF_MT_AVG_BITRATE =
            new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

        public static Guid MF_MT_FRAME_SIZE =
            new("1652c33d-d6b2-4012-b834-72030849a37d");

        public static Guid MF_MT_FRAME_RATE =
            new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

        public static Guid MF_MT_PIXEL_ASPECT_RATIO =
            new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

        public static Guid MF_MT_INTERLACE_MODE =
            new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

        public static Guid MFMediaType_Video =
            new("73646976-0000-0010-8000-00AA00389B71");

        public static Guid MFVideoFormat_H264 =
            new("34363248-0000-0010-8000-00AA00389B71");

        public static Guid MFVideoFormat_NV12 =
            new("3231564E-0000-0010-8000-00AA00389B71");

        // ─── MF API (all in mfplat.dll) ───

        [DllImport("mfplat.dll")]
        public static extern int MFStartup(int version, int dwFlags);

        [DllImport("mfplat.dll")]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll")]
        public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

        [DllImport("mfplat.dll")]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFMediaType);

        [DllImport("mfplat.dll")]
        public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

        [DllImport("mfplat.dll")]
        public static extern int MFCreateSample(out IntPtr ppIMFSample);

        // MFTEnumEx is in mfplat.dll
        [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")]
        public static extern int MFTEnumEx(
            [In] Guid guidCategory,
            int flags,
            IntPtr pInputType,
            IntPtr pOutputType,
            out IntPtr pppMFTActivate,
            out int pnumMFTActivate);

        [DllImport("mfreadwrite.dll")]
        public static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
            IntPtr pByteStream,
            IntPtr pAttributes,
            out IntPtr ppSinkWriter);
    }
}
