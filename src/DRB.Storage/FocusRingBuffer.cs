using Microsoft.Extensions.Logging;

namespace DRB.Storage;

/// <summary>
/// Thread-safe ring buffer that manages fixed-duration H.264 MP4 segments on disk.
/// Evicts the oldest segments when the segment count exceeds <see cref="MaxSegments"/>.
/// </summary>
public sealed class FocusRingBuffer
{
    private const int MaxSegments = 6;

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Queue<string> _segments = new();

    public FocusRingBuffer(string directory, ILogger logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_directory);
        RebuildFromDisk();
    }

    /// <summary>
    /// Adds a completed segment path and evicts the oldest if over limit.
    /// </summary>
    public void AddSegment(string path)
    {
        lock (_lock)
        {
            _segments.Enqueue(path);
            _logger.LogDebug("Focus buffer: added '{Path}', " +
                "count={Count}, max={Max}", path, _segments.Count, MaxSegments);

            while (_segments.Count > MaxSegments)
            {
                var old = _segments.Dequeue();
                _logger.LogInformation("Focus buffer evicting: '{Path}'", old);
                if (File.Exists(old))
                {
                    File.Delete(old);
                    _logger.LogInformation("Focus buffer deleted file: '{Path}'", old);
                }
                else
                {
                    _logger.LogWarning("Focus buffer evict: file not found: '{Path}'", old);
                }
            }

            _logger.LogDebug("Focus buffer after eviction: count={Count}", 
                _segments.Count);
        }
    }

    /// <summary>Number of segments currently in the buffer.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _segments.Count;
            }
        }
    }

    // ──────────────────── Crash Recovery ──────────────────────────

    /// <summary>
    /// Scans the directory for existing .mp4 files and rebuilds the segment list.
    /// File names are expected to follow the pattern yyyyMMdd_HHmmss_fff.mp4.
    /// Sorts by file creation time for accurate chronological ordering.
    /// </summary>
    private void RebuildFromDisk()
    {
        var files = Directory.GetFiles(_directory, "*.mp4");
        if (files.Length == 0) return;

        // Sort by file creation time to ensure chronological order
        Array.Sort(files, (a, b) =>
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            return infoA.CreationTimeUtc.CompareTo(infoB.CreationTimeUtc);
        });

        int recovered = 0;
        foreach (var file in files)
        {
            _segments.Enqueue(file);
            recovered++;
        }

        // Evict excess after recovery.
        while (_segments.Count > MaxSegments)
        {
            var old = _segments.Dequeue();
            if (File.Exists(old)) File.Delete(old);
            _logger.LogDebug("Evicted focus segment on recovery: {Path}", old);
        }

        if (recovered > 0)
            _logger.LogInformation("Recovered {Count} segments from disk in {Dir}.", recovered, _directory);
    }
}
