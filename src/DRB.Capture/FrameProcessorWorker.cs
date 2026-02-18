using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class FrameProcessor : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ILogger<FrameProcessor> _logger;

    public FrameProcessor(IAppChannels channels, ILogger<FrameProcessor> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FrameProcessor thread starting (stub).");

        await foreach (var frame in _channels.CaptureToProcessor.Reader.ReadAllAsync(stoppingToken))
        {
            // Stub: overlay-friendly frame and OCR job creation.
            var processed = new ProcessedFrame(frame.Pixels, frame.TimestampTicks);

            await _channels.ProcessorToOverlay.Writer.WriteAsync(processed, stoppingToken)
                .ConfigureAwait(false);

            var ocrJob = new OcrJob(processed);
            await _channels.ProcessorToOcr.Writer.WriteAsync(ocrJob, stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("FrameProcessor thread stopping.");
    }
}

