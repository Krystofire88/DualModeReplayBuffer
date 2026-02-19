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

    public StorageManager(
        IAppChannels channels,
        IClipStorage clipStorage,
        FocusRingBuffer ringBuffer,
        ILogger<StorageManager> logger)
    {
        _channels = channels;
        _clipStorage = clipStorage;
        _ringBuffer = ringBuffer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageManager thread starting.");

        await _clipStorage.InitializeAsync(stoppingToken).ConfigureAwait(false);

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

        _logger.LogInformation("StorageManager thread stopping.");
    }
}
