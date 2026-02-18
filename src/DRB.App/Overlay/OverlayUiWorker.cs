using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.App.Overlay;

public sealed class OverlayUI : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly ILogger<OverlayUI> _logger;

    public OverlayUI(IAppChannels channels, ILogger<OverlayUI> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OverlayUI thread starting (stub).");

        await foreach (ProcessedFrame frame in _channels.ProcessorToOverlay.Reader.ReadAllAsync(stoppingToken))
        {
            // Stub: update overlay visuals with processed frames.
        }

        _logger.LogInformation("OverlayUI thread stopping.");
    }
}

