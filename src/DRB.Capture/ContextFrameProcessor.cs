using Microsoft.Extensions.Logging;

namespace DRB.Capture;

/// <summary>
/// Perceptual-hash based change detection for Context Mode.
/// Computes a 256-bit pHash (16×16) from each incoming BGRA frame and compares it
/// against the last stored hash using Hamming distance.
/// </summary>
public sealed class ContextFrameProcessor
{
    /// <summary>
    /// Hamming distance threshold: if the distance between two hashes exceeds
    /// this value the frame is considered "changed".
    /// 98% similar = at most 2% of 256 bits differ = max 5 bits different.
    /// </summary>
    private const int ChangeThreshold = 5;

    private readonly ILogger _logger;
    private ulong[]? _lastHash;
    private bool _hasLastHash;

    public ContextFrameProcessor(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the last computed perceptual hash (4 ulongs = 256 bits).
    /// Valid after at least one call to <see cref="HasChanged(byte[], int, int, out ulong[])"/>.
    /// </summary>
    public ulong[] LastHash => _lastHash!;

    /// <summary>
    /// Gets a compact single-ulong representation of the last hash (XOR of all 4 parts).
    /// Lossy but sufficient for storage in the database.
    /// </summary>
    public ulong LastHashCompact => _lastHash != null
        ? _lastHash[0] ^ _lastHash[1] ^ _lastHash[2] ^ _lastHash[3]
        : 0;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="bgraFrame"/> is sufficiently
    /// different from the last frame that was accepted.
    /// The first frame is always accepted.
    /// When the frame is accepted the internal hash is updated.
    /// </summary>
    public bool HasChanged(byte[] bgraFrame, int width, int height, out ulong[] currentHash)
    {
        currentHash = ComputePHash(bgraFrame, width, height);

        if (!_hasLastHash)
        {
            _lastHash = currentHash;
            _hasLastHash = true;
            _logger.LogDebug("Context: first frame accepted (pHash={Hash:X16}{Hash2:X16}{Hash3:X16}{Hash4:X16}).",
                currentHash[0], currentHash[1], currentHash[2], currentHash[3]);
            return true;
        }

        int distance = HammingDistance(_lastHash!, currentHash);

        if (distance > ChangeThreshold)
        {
            _logger.LogDebug(
                "Context: frame changed (distance={Distance}).", distance);
            _lastHash = currentHash;
            return true;
        }

        _logger.LogTrace(
            "Context: frame unchanged (distance={Distance}), discarding.",
            distance);
        return false;
    }

    /// <summary>
    /// Convenience overload that discards the computed hash.
    /// </summary>
    public bool HasChanged(byte[] bgraFrame, int width, int height)
        => HasChanged(bgraFrame, width, height, out _);

    // ──────────────────────────── pHash computation ────────────────────────────

    /// <summary>
    /// Computes a 256-bit perceptual hash (16×16):
    /// 1. Downscale the BGRA frame to 16×16 grayscale.
    /// 2. Compute the mean of all 256 pixel values.
    /// 3. Each bit = 1 if the pixel is above the mean, 0 otherwise.
    /// 4. Returns 4 ulongs (256 bits total).
    /// </summary>
    private static ulong[] ComputePHash(byte[] bgraFrame, int width, int height)
    {
        const int hashW = 16;
        const int hashH = 16;
        const int hashSize = hashW * hashH;

        // Step 1: Downscale to 16×16 grayscale using nearest-neighbour sampling.
        var gray = new float[hashSize];
        float xRatio = width / (float)hashW;
        float yRatio = height / (float)hashH;

        for (int y = 0; y < hashH; y++)
        {
            for (int x = 0; x < hashW; x++)
            {
                int srcX = (int)(x * xRatio);
                int srcY = (int)(y * yRatio);
                int idx = (srcY * width + srcX) * 4; // BGRA: B=0, G=1, R=2

                byte b = bgraFrame[idx + 0];
                byte g = bgraFrame[idx + 1];
                byte r = bgraFrame[idx + 2];

                // ITU-R BT.709 luma
                gray[y * hashW + x] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            }
        }

        // Step 2: Compute mean.
        float mean = 0;
        for (int i = 0; i < hashSize; i++)
            mean += gray[i];
        mean /= hashSize;

        // Step 3: Build 256-bit hash packed into 4 ulongs.
        var hash = new ulong[4];
        for (int i = 0; i < hashSize; i++)
        {
            if (gray[i] > mean)
            {
                hash[i / 64] |= 1UL << (i % 64);
            }
        }

        return hash;
    }

    // ──────────────────────────── Hamming distance ─────────────────────────────

    /// <summary>
    /// Returns the number of differing bits between two 256-bit hashes (4 ulongs).
    /// </summary>
    private static int HammingDistance(ulong[] a, ulong[] b)
    {
        int distance = 0;
        for (int i = 0; i < 4; i++)
        {
            distance += System.Numerics.BitOperations.PopCount(a[i] ^ b[i]);
        }
        return distance;
    }
}
