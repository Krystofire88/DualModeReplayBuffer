using DRB.Core.Models;
using Microsoft.Extensions.Logging;

namespace DRB.Storage;

/// <summary>
/// Thread-safe ring buffer that manages fixed-duration H.264 MP4 segments on disk.
/// Evicts the oldest segments when total duration exceeds <see cref="_maxDuration"/>.
/// On startup, scans the directory for existing segments to support crash recovery.
/// </summary>
public sealed class FocusRingBuffer
{
    private readonly string _directory;
    private readonly TimeSpan _maxDuration;
    private readonly ILogger _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly LinkedList<VideoSegment> _segments = new();

    public FocusRingBuffer(string directory, TimeSpan maxDuration, ILogger logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _maxDuration = maxDuration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_directory);
        RebuildFromDisk();
    }

    /// <summary>
    /// Adds a completed segment and evicts the oldest segments if the total
    /// buffered duration exceeds <see cref="_maxDuration"/>.
    /// </summary>
    public void AddSegment(VideoSegment segment)
    {
        _lock.EnterWriteLock();
        try
        {
            _segments.AddLast(segment);
            EvictExcess();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns all segments whose time range overlaps [from, to].
    /// </summary>
    public IReadOnlyList<VideoSegment> GetSegmentsForRange(DateTime from, DateTime to)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<VideoSegment>();
            foreach (var seg in _segments)
            {
                var segEnd = seg.Start + seg.Duration;
                // Overlap check: segment overlaps [from, to] if segStart < to && segEnd > from
                if (seg.Start < to && segEnd > from)
                    result.Add(seg);
            }
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Total buffered duration across all segments.</summary>
    public TimeSpan TotalDuration
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                var total = TimeSpan.Zero;
                foreach (var seg in _segments)
                    total += seg.Duration;
                return total;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Number of segments currently in the buffer.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _segments.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ──────────────────── Crash Recovery ──────────────────────────

    /// <summary>
    /// Scans the directory for existing .mp4 files and rebuilds the segment list.
    /// File names are expected to follow the pattern yyyyMMdd_HHmmss.mp4.
    /// </summary>
    private void RebuildFromDisk()
    {
        var files = Directory.GetFiles(_directory, "*.mp4");
        if (files.Length == 0) return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        int recovered = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (TryParseSegmentTimestamp(fileName, out var start))
            {
                // Estimate duration from file — we don't have the exact duration,
                // so we use a heuristic based on the gap to the next file or a default.
                var segment = new VideoSegment(file, start, TimeSpan.Zero);
                _segments.AddLast(segment);
                recovered++;
            }
        }

        // Now fix up durations: each segment's duration = next segment's start - this segment's start.
        // For the last segment, use a default estimate.
        var node = _segments.First;
        while (node is not null)
        {
            if (node.Next is not null)
            {
                var duration = node.Next.Value.Start - node.Value.Start;
                if (duration > TimeSpan.Zero)
                {
                    node.Value = node.Value with { Duration = duration };
                }
            }
            else
            {
                // Last segment — estimate from file size or use a default.
                // Use 5 seconds as a reasonable default for the last segment.
                node.Value = node.Value with { Duration = TimeSpan.FromSeconds(5) };
            }
            node = node.Next;
        }

        // Evict excess after recovery.
        EvictExcess();

        if (recovered > 0)
            _logger.LogInformation("Recovered {Count} segments from disk in {Dir}.", recovered, _directory);
    }

    private static bool TryParseSegmentTimestamp(string fileName, out DateTime timestamp)
    {
        // Expected format: yyyyMMdd_HHmmss
        return DateTime.TryParseExact(
            fileName,
            "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    // ──────────────────── Eviction ───────────────────────────────

    private void EvictExcess()
    {
        var total = TimeSpan.Zero;
        foreach (var seg in _segments)
            total += seg.Duration;

        while (total > _maxDuration && _segments.First is not null)
        {
            var oldest = _segments.First.Value;
            _segments.RemoveFirst();
            total -= oldest.Duration;

            // Delete the file from disk.
            try
            {
                if (File.Exists(oldest.Path))
                {
                    File.Delete(oldest.Path);
                    _logger.LogDebug("Evicted segment: {Path}", oldest.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete evicted segment: {Path}", oldest.Path);
            }
        }
    }
}
