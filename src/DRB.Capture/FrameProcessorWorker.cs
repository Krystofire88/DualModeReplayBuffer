using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using DRB.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DRB.Capture;

public sealed class FrameProcessor : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;
    private readonly ILogger<FrameProcessor> _logger;
    private readonly ContextFrameProcessor _contextProcessor;
    private readonly ContextIndex _contextIndex;
    private DateTime _lastCaptureTime = DateTime.MinValue;

    public FrameProcessor(IAppChannels channels, Config config, ILogger<FrameProcessor> logger, ContextFrameProcessor contextProcessor, ContextIndex contextIndex)
    {
        _channels = channels;
        _config = config;
        _logger = logger;
        _contextProcessor = contextProcessor;
        _contextIndex = contextIndex;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FrameProcessor thread starting.");
        _logger.LogInformation("FrameProcessor capture mode: {CaptureMode}", _config.CaptureMode);

        await foreach (var frame in _channels.CaptureToProcessor.Reader.ReadAllAsync(stoppingToken))
        {
            if (_config.CaptureMode == CaptureMode.Context)
            {
                await ProcessContextFrameAsync(frame, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await ProcessFocusFrameAsync(frame, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("FrameProcessor thread stopping.");
    }

    /// <summary>
    /// Focus Mode: forward frame to overlay and encoder, optionally submit OCR job.
    /// </summary>
    private async Task ProcessFocusFrameAsync(RawFrame frame, CancellationToken ct)
    {
        // Create an overlay-friendly processed frame.
        var processed = new ProcessedFrame(frame.Pixels, frame.TimestampTicks);

        await _channels.ProcessorToOverlay.Writer.WriteAsync(processed, ct)
            .ConfigureAwait(false);

        // In Focus Mode, also forward the raw frame to the encoder channel
        // so it gets encoded into H.264 segments.
        await _channels.CaptureToEncoder.Writer.WriteAsync(frame, ct)
            .ConfigureAwait(false);

        // Submit OCR job if enabled.
        if (_config.OcrEnabled)
        {
            var ocrJob = new OcrJob(processed);
            await _channels.ProcessorToOcr.Writer.WriteAsync(ocrJob, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Context Mode: run pHash change detection, save changed frames as JPEG,
    /// and push a <see cref="ContextFrame"/> record to the ProcessorToStorage channel.
    /// Captures at 1fps and only saves frames that differ from the previous one.
    /// </summary>
    private async Task ProcessContextFrameAsync(RawFrame frame, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        
        // Throttle to 1fps - wait if we've captured within the last second
        var timeSinceLastCapture = now - _lastCaptureTime;
        _logger.LogDebug("Context throttle: timeSinceLastCapture={Time}ms, lastCapture={Last}", 
            timeSinceLastCapture.TotalMilliseconds, _lastCaptureTime);
        
        if (timeSinceLastCapture < TimeSpan.FromSeconds(1))
        {
            _logger.LogDebug("Context throttle: dropping frame, too soon");
            return; // Drop frame, wait for next second
        }
        
        if (!_contextProcessor.HasChanged(frame.Pixels, frame.Width, frame.Height))
        {
            _logger.LogDebug("Context throttle: frame unchanged, dropping");
            // Frame is visually identical to the last stored one – discard.
            return;
        }

        _lastCaptureTime = now;
        _logger.LogDebug("Context throttle: capturing frame, enough time passed and frame changed");
        var timestamp = now;
        string fileName = $"{timestamp:yyyyMMdd_HHmmss_fff}.jpg";
        string filePath = Path.Combine(AppPaths.ContextBufferFolder, fileName);

        try
        {
            // Ensure the directory exists (should already, but be defensive).
            Directory.CreateDirectory(AppPaths.ContextBufferFolder);

            // Encode BGRA byte[] → JPEG using ImageSharp.
            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(frame.Pixels, frame.Width, frame.Height);
            await image.SaveAsJpegAsync(filePath, new JpegEncoder { Quality = 85 }, ct)
                .ConfigureAwait(false);

            _logger.LogDebug("Context: saved snapshot {Path}.", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Context: failed to save snapshot {Path}.", filePath);
            return;
        }

        // Push frame downstream for storage.
        var contextFrameRecord = new ContextFrame(filePath, timestamp, _contextProcessor.LastHashCompact);
        await _channels.ProcessorToStorage.Writer.WriteAsync(contextFrameRecord, ct)
            .ConfigureAwait(false);
    }
}
