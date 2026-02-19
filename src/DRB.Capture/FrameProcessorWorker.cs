using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class FrameProcessor : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly ILogger<FrameProcessor> _logger;

    public FrameProcessor(IAppChannels channels, Config config, ILogger<FrameProcessor> logger)
    {
        _channels = channels;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FrameProcessor thread starting.");

        await foreach (var frame in _channels.CaptureToProcessor.Reader.ReadAllAsync(stoppingToken))
        {
            // Create an overlay-friendly processed frame.
            var processed = new ProcessedFrame(frame.Pixels, frame.TimestampTicks);

            await _channels.ProcessorToOverlay.Writer.WriteAsync(processed, stoppingToken)
                .ConfigureAwait(false);

            // In Focus Mode, also forward the raw frame to the encoder channel
            // so it gets encoded into H.264 segments.
            if (_config.CaptureMode == CaptureMode.Focus)
            {
                await _channels.CaptureToEncoder.Writer.WriteAsync(frame, stoppingToken)
                    .ConfigureAwait(false);
            }

            // Submit OCR job if enabled.
            if (_config.OcrEnabled)
            {
                var ocrJob = new OcrJob(processed);
                await _channels.ProcessorToOcr.Writer.WriteAsync(ocrJob, stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogInformation("FrameProcessor thread stopping.");
    }
}
