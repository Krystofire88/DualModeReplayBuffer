using System.Threading.Channels;
using DRB.Core.Models;

namespace DRB.Core.Messaging;

public interface IAppChannels
{
    Channel<RawFrame> CaptureToEncoder { get; }
    Channel<RawFrame> CaptureToProcessor { get; }

    Channel<EncodedFrame> EncoderToStorage { get; }

    Channel<ProcessedFrame> ProcessorToOverlay { get; }
    Channel<OcrJob> ProcessorToOcr { get; }

    Channel<OcrResult> OcrToOverlay { get; }

    Channel<ClipRequest> OverlayToStorage { get; }
}

