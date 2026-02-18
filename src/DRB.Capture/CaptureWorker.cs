using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class Capture : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ILogger<Capture> _logger;

    public Capture(IAppChannels channels, ILogger<Capture> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Capture thread starting (Desktop Duplication API stub).");

        var encoderWriter = _channels.CaptureToEncoder.Writer;
        var processorWriter = _channels.CaptureToProcessor.Writer;

        // Stub loop – replace with Desktop Duplication API capture.
        while (!stoppingToken.IsCancellationRequested)
        {
            var dummyPixels = Array.Empty<byte>();
            var frame = new RawFrame(dummyPixels, 0, 0, DateTime.UtcNow.Ticks);

            await encoderWriter.WriteAsync(frame, stoppingToken).ConfigureAwait(false);
            await processorWriter.WriteAsync(frame, stoppingToken).ConfigureAwait(false);

            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Capture thread stopping.");
    }
}

