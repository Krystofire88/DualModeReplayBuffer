using DRB.Core;
using DRB.Core.Messaging;
using DRB.Core.Models;
using Microsoft.Extensions.Hosting;

namespace DRB.Capture;

public sealed class Encoder : BackgroundService
{
    private readonly IAppChannels _channels;
    private readonly Config _config;

    public Encoder(IAppChannels channels, Config config)
    {
        _channels = channels;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var encoder = new HardwareVideoEncoder(_config);

        // Forward completed segments to the storage channel.
        encoder.OnSegmentComplete += segment =>
        {
            // Wrap the segment data as an EncodedFrame for the storage pipeline.
            // The EncodedFrame.Data carries the segment path as UTF-8 bytes (lightweight marker).
            var marker = System.Text.Encoding.UTF8.GetBytes(segment.Path);
            var encoded = new EncodedFrame(marker, segment.Start.Ticks);
            _channels.EncoderToStorage.Writer.TryWrite(encoded);
        };

        await foreach (var frame in _channels.CaptureToEncoder.Reader.ReadAllAsync(stoppingToken))
        {
            // Stop processing if encoder has failed
            if (encoder.EncoderFailed)
            {
                break;
            }

            try
            {
                encoder.PushFrame(frame.Pixels, frame.TimestampTicks);
            }
            catch
            {
            }
        }

        // Flush the final segment on shutdown.
        encoder.Flush();
    }
}
