using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class Encoder : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly ILogger<Encoder> _logger;

    public Encoder(IAppChannels channels, Config config, ILogger<Encoder> logger)
    {
        _channels = channels;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Encoder thread starting (Media Foundation H.264).");

        using var encoder = new HardwareVideoEncoder(_config, _logger);

        // Forward completed segments to the storage channel.
        encoder.OnSegmentComplete += segment =>
        {
            // Wrap the segment data as an EncodedFrame for the storage pipeline.
            // The EncodedFrame.Data carries the segment path as UTF-8 bytes (lightweight marker).
            var marker = System.Text.Encoding.UTF8.GetBytes(segment.Path);
            var encoded = new EncodedFrame(marker, segment.Start.Ticks);
            _channels.EncoderToStorage.Writer.TryWrite(encoded);
        };

        await foreach (var frame in _channels.CaptureToEncoder.Reader.ReadAllAsync(stoppingToken))
        {
            // Stop processing if encoder has failed
            if (encoder.EncoderFailed)
            {
                _logger.LogError("Encoder has failed unrecoverably. Stopping encoder worker.");
                break;
            }

            try
            {
                encoder.PushFrame(frame.Pixels, frame.TimestampTicks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error encoding frame.");
            }
        }

        // Flush the final segment on shutdown.
        encoder.Flush();

        _logger.LogInformation("Encoder thread stopping.");
    }
}
