using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Storage;

public sealed class StorageManager : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly IClipStorage _clipStorage;
    private readonly FocusRingBuffer _ringBuffer;
    private readonly ILogger<StorageManager> _logger;
    private readonly ContextIndex? _contextIndex;

    public StorageManager(
        IAppChannels channels,
        IClipStorage clipStorage,
        FocusRingBuffer ringBuffer,
        ILogger<StorageManager> logger,
        ContextIndex? contextIndex = null)
    {
        _channels = channels;
        _clipStorage = clipStorage;
        _ringBuffer = ringBuffer;
        _logger = logger;
        _contextIndex = contextIndex;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageManager thread starting.");

        await _clipStorage.InitializeAsync(stoppingToken).ConfigureAwait(false);

        // Process Focus Mode: encoded frames and clip requests.
        var focusTask = ProcessFocusSegments(stoppingToken);

        // Process Context Mode: context frames from ProcessorToStorage channel.
        var contextTask = ProcessContextFrames(stoppingToken);

        await Task.WhenAll(focusTask, contextTask).ConfigureAwait(false);

        _logger.LogInformation("StorageManager thread stopping.");
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
                    // Estimate duration from the file — the encoder sets this properly
                    // via the OnSegmentComplete event, but here we receive the marker.
                    // For now, use the ring buffer's AddSegment which will be called
                    // directly by the encoder event. This path is a fallback.
                    _logger.LogDebug("Received encoded segment marker: {Path}", segmentPath);
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

            // Enforce 2-minute rolling window.
            _contextIndex.DeleteBefore(DateTime.UtcNow - TimeSpan.FromMinutes(2));

            _logger.LogDebug("Context frame indexed: {Path}", frame.Path);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _contextIndex?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
