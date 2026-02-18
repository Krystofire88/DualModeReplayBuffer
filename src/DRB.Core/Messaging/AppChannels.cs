using System.Threading.Channels;
using DRB.Core.Models;

namespace DRB.Core.Messaging;

public sealed class AppChannels : IAppChannels
{
    private static BoundedChannelOptions Bounded(int capacity) => new(capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false
    };

    public AppChannels()
    {
        CaptureToEncoder = Channel.CreateBounded<RawFrame>(Bounded(256));
        CaptureToProcessor = Channel.CreateBounded<RawFrame>(Bounded(256));
        EncoderToStorage = Channel.CreateBounded<EncodedFrame>(Bounded(256));
        ProcessorToOverlay = Channel.CreateBounded<ProcessedFrame>(Bounded(256));
        ProcessorToOcr = Channel.CreateBounded<OcrJob>(Bounded(256));
        OcrToOverlay = Channel.CreateBounded<OcrResult>(Bounded(256));
        OverlayToStorage = Channel.CreateBounded<ClipRequest>(Bounded(64));
    }

    public Channel<RawFrame> CaptureToEncoder { get; }
    public Channel<RawFrame> CaptureToProcessor { get; }
    public Channel<EncodedFrame> EncoderToStorage { get; }
    public Channel<ProcessedFrame> ProcessorToOverlay { get; }
    public Channel<OcrJob> ProcessorToOcr { get; }
    public Channel<OcrResult> OcrToOverlay { get; }
    public Channel<ClipRequest> OverlayToStorage { get; }
}

