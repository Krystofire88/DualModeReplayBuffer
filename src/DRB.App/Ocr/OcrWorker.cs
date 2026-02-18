using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.App.Ocr;

public sealed class OcrWorker : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ILogger<OcrWorker> _logger;

    public OcrWorker(IAppChannels channels, ILogger<OcrWorker> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OcrWorker thread starting (stub).");

        await foreach (OcrJob job in _channels.ProcessorToOcr.Reader.ReadAllAsync(stoppingToken))
        {
            // Stub: perform OCR here.
            var result = new OcrResult(job, string.Empty);

            await _channels.OcrToOverlay.Writer.WriteAsync(result, stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("OcrWorker thread stopping.");
    }
}

