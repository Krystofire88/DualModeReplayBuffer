using System.Diagnostics;
using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;

namespace DRB.Capture;

public sealed class CaptureWorker : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly IPauseCapture _pauseCapture;
    private readonly ICaptureController _captureController;

    private static readonly TimeSpan ReinitDelay = TimeSpan.FromSeconds(1);

    public CaptureWorker(IAppChannels channels, Config config, IPauseCapture pauseCapture, ICaptureController captureController)
    {
        _channels = channels;
        _config = config;
        _pauseCapture = pauseCapture;
        _captureController = captureController;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processorWriter = _channels.CaptureToProcessor.Writer;

        using var captureService = new DxgiCaptureService(_config);

        // Outer loop: handles re-initialization after access-lost events.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                captureService.Initialize();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
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
                await Task.Delay(ReinitDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
