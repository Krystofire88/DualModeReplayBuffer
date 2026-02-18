using DRB.Core.Models;

namespace DRB.Storage;

public interface IClipStorage
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveEncodedFrameAsync(EncodedFrame frame, CancellationToken cancellationToken = default);

    Task SaveClipAsync(ClipRequest request, CancellationToken cancellationToken = default);
}

