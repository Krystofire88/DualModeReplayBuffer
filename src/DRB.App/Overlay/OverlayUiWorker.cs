using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.App.Overlay;

public sealed class OverlayUI : BackgroundService
{
    private readonly ILogger<OverlayUI> _logger;

    public OverlayUI(ILogger<OverlayUI> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OverlayUI starting.");
        return Task.CompletedTask;
    }
}
