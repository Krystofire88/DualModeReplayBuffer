using System;
using System.IO;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;

namespace DRB.Storage;

public sealed class StorageManager : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly IClipStorage _clipStorage;
    private readonly FocusRingBuffer _ringBuffer;
    private readonly ContextIndex? _contextIndex;

    public StorageManager(
        IAppChannels channels,
        IClipStorage clipStorage,
        FocusRingBuffer ringBuffer,
        ContextIndex? contextIndex = null)
    {
        _channels = channels;
        _clipStorage = clipStorage;
        _ringBuffer = ringBuffer;
        _contextIndex = contextIndex;
    }

    private void ClearBuffersOnStartup()
    {
        string focusBufferPath = DRB.Core.AppPaths.FocusBufferFolder;
        string contextBufferPath = DRB.Core.AppPaths.ContextBufferFolder;
        
        // Clear focus buffer files
        if (Directory.Exists(focusBufferPath))
        {
            foreach (var f in Directory.GetFiles(focusBufferPath, "*.mp4"))
            {
                try { File.Delete(f); }
                catch { }
            }
        }
        
        // Clear context buffer files
        if (Directory.Exists(contextBufferPath))
        {
            foreach (var f in Directory.GetFiles(contextBufferPath, "*.jpg"))
            {
                try { File.Delete(f); }
                catch { }
            }
        }
        
        _contextIndex?.ClearAll();
        
        // Clear in-memory ring buffer
        _ringBuffer.Clear();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _clipStorage.InitializeAsync(stoppingToken).ConfigureAwait(false);

        // Clear buffers on startup
        ClearBuffersOnStartup();

        // Reconcile DB with disk before processing
        _contextIndex?.ReconcileWithDisk();

        // Process Focus Mode: encoded frames and clip requests.
        var focusTask = ProcessFocusSegments(stoppingToken);

        // Process Context Mode: context frames from ProcessorToStorage channel.
        var contextTask = ProcessContextFrames(stoppingToken);

        await Task.WhenAll(focusTask, contextTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Process Focus Mode: encoded frames from encoder and clip requests from overlay.
    /// </summary>
    private async Task ProcessFocusSegments(CancellationToken stoppingToken)
    {
        var encodedFrames = _channels.EncoderToStorage.Reader;
        var clipRequests = _channels.OverlayToStorage.Reader;

        // Process encoded frame markers (segment path notifications from the encoder).
        var encodedTask = Task.Run(async () =>
        {
            await foreach (var frame in encodedFrames.ReadAllAsync(stoppingToken))
            {
                // The encoder sends segment path as UTF-8 bytes in EncodedFrame.Data.
                var segmentPath = System.Text.Encoding.UTF8.GetString(frame.Data);
                if (File.Exists(segmentPath))
                {
                    var start = new DateTime(frame.TimestampTicks, DateTimeKind.Utc);
                    // Add segment to the ring buffer for count-based eviction.
                    _ringBuffer.AddSegment(segmentPath);
                }

                await _clipStorage.SaveEncodedFrameAsync(frame, stoppingToken)
                    .ConfigureAwait(false);
            }
        }, stoppingToken);

        // Process clip requests from the overlay.
        var clipTask = Task.Run(async () =>
        {
            await foreach (var request in clipRequests.ReadAllAsync(stoppingToken))
            {
                await _clipStorage.SaveClipAsync(request, stoppingToken)
                    .ConfigureAwait(false);
            }
        }, stoppingToken);

        await Task.WhenAll(encodedTask, clipTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Process Context Mode: context frames from ProcessorToStorage channel.
    /// </summary>
    private async Task ProcessContextFrames(CancellationToken stoppingToken)
    {
        if (_contextIndex == null)
        {
            // ContextIndex not available (running in Focus mode).
            return;
        }

        await foreach (var frame in _channels.ProcessorToStorage.Reader.ReadAllAsync(stoppingToken))
        {
            _contextIndex.Insert(frame);

            // Enforce 120 frame rolling window.
            int limit = 120;
            _contextIndex.EnforceMaxFrames(limit);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _contextIndex?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
