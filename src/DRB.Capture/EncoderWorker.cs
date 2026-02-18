using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class Encoder : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ILogger<Encoder> _logger;

    public Encoder(IAppChannels channels, ILogger<Encoder> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Encoder thread starting (stub).");

        await foreach (var frame in _channels.CaptureToEncoder.Reader.ReadAllAsync(stoppingToken))
        {
            // Stub: replace with actual encoder implementation.
            var encoded = new EncodedFrame(frame.Pixels, frame.TimestampTicks);
            await _channels.EncoderToStorage.Writer.WriteAsync(encoded, stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Encoder thread stopping.");
    }
}

