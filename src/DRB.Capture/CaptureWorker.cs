using System.Diagnostics;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DRB.Capture;

public sealed class CaptureWorker : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly IPauseCapture _pauseCapture;
    private readonly ICaptureController _captureController;
    private readonly ILogger<CaptureWorker> _logger;

    private static readonly TimeSpan ReinitDelay = TimeSpan.FromSeconds(1);

    public CaptureWorker(IAppChannels channels, Config config, IPauseCapture pauseCapture, ICaptureController captureController, ILogger<CaptureWorker> logger)
    {
        _channels = channels;
        _config = config;
        _pauseCapture = pauseCapture;
        _captureController = captureController;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Capture thread starting (DXGI Desktop Duplication).");
        _logger.LogInformation("Capture mode: {CaptureMode}", _config.CaptureMode);

        var processorWriter = _channels.CaptureToProcessor.Writer;

        using var captureService = new DxgiCaptureService(_config, _logger);

        // Outer loop: handles re-initialization after access-lost events.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                captureService.Initialize();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to initialize Desktop Duplication. Retrying in {Delay}…", ReinitDelay);
                await Task.Delay(ReinitDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // Inner loop: tight frame-acquisition loop.
            var stopwatch = Stopwatch.StartNew();
            long lastFrameTicks = 0;

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!_captureController.IsRunning)
                    {
                        await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    if (_pauseCapture.IsPaused)
                    {
                        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var frameInterval = captureService.GetFrameInterval();

                    // Rate-limit: skip if not enough time has elapsed since the last emitted frame.
                    long elapsedTicks = stopwatch.ElapsedTicks;
                    long intervalTicks = (long)(frameInterval.TotalSeconds * Stopwatch.Frequency);

                    if (elapsedTicks - lastFrameTicks < intervalTicks)
                    {
                        // Yield briefly so we don't burn CPU while waiting for the next frame window.
                        await Task.Delay(1, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    RawFrame? frame;
                    try
                    {
                        frame = captureService.TryAcquireFrame();
                    }
                    catch (DxgiAccessLostException)
                    {
                        _logger.LogWarning("DXGI access lost (desktop switch / UAC). Re-initializing…");
                        break; // break inner loop → re-init in outer loop
                    }

                    if (frame is null)
                    {
                        // Timeout / no new frame – loop back and try again.
                        continue;
                    }

                    lastFrameTicks = stopwatch.ElapsedTicks;

                    // Push raw frame to the processor channel.
                    // FrameProcessor routes to encoder (Focus) or context pipeline (Context).
                    await processorWriter.WriteAsync(frame, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown requested – fall through.
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Unexpected error in capture loop. Re-initializing in {Delay}…", ReinitDelay);
                await Task.Delay(ReinitDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Capture thread stopping.");
    }
}
