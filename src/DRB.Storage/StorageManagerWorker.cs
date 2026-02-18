using DRB.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Storage;

public sealed class StorageManager : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly IClipStorage _clipStorage;
    private readonly ILogger<StorageManager> _logger;

    public StorageManager(
        IAppChannels channels,
        IClipStorage clipStorage,
        ILogger<StorageManager> logger)
    {
        _channels = channels;
        _clipStorage = clipStorage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageManager thread starting (SQLite ring buffer stub).");

        await _clipStorage.InitializeAsync(stoppingToken).ConfigureAwait(false);

        var encodedFrames = _channels.EncoderToStorage.Reader;
        var clipRequests = _channels.OverlayToStorage.Reader;

        var encodedTask = Task.Run(async () =>
        {
            await foreach (var frame in encodedFrames.ReadAllAsync(stoppingToken))
            {
                await _clipStorage.SaveEncodedFrameAsync(frame, stoppingToken)
                    .ConfigureAwait(false);
            }
        }, stoppingToken);

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

