using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DRB.Core;

namespace DRB.Storage;

/// <summary>
/// Thread-safe ring buffer that manages fixed-duration H.264 MP4 segments on disk.
/// Evicts the oldest segments when the segment count exceeds <see cref="MaxSegments"/>.
/// </summary>
public sealed class FocusRingBuffer
{
    private const int MaxSegments = 6;

    private readonly string _directory;
    private readonly object _lock = new();
    private readonly Queue<string> _segments = new();

    public FocusRingBuffer(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));

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

            while (_segments.Count > MaxSegments)
            {
                var old = _segments.Dequeue();
                if (File.Exists(old))
                {
                    File.Delete(old);
                }
            }
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

    /// <summary>
    /// Returns a copy of current segment paths in order (oldest → newest).
    /// Filters out incomplete segments (< 100KB or being written).
    /// </summary>
    public IReadOnlyList<string> GetSegmentsCopy()
    {
        lock (_lock)
            return _segments
                .Where(p => {
                    try {
                        var fi = new FileInfo(p);
                        // Must exist, be over 100KB, and not modified in last 2 seconds (not currently being written)
                        return fi.Exists 
                            && fi.Length > 102_400
                            && (DateTime.Now - fi.LastWriteTime).TotalSeconds > 2;
                    }
                    catch { return false; }
                })
                .ToArray(); // Queue<T>.ToArray() is oldest→newest
    }

    /// <summary>
    /// Clears all segments from the buffer.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
            _segments.Clear();
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
        }
    }

    // ──────────────────── Thumbnail Extraction ─────────────────────────

    /// <summary>
    /// Extracts a thumbnail image from an MP4 segment using FFmpeg.
    /// Returns the path to the cached thumbnail, or null if extraction failed.
    /// </summary>
    public async Task<string?> ExtractThumbnailAsync(string segmentPath)
    {
        string thumbPath = Path.Combine(
            Path.GetTempPath(),
            $"drb_thumb_{Path.GetFileNameWithoutExtension(segmentPath)}.jpg");

        if (File.Exists(thumbPath))
            return thumbPath;

        string ffmpeg = FfmpegPaths.FindExecutable();

        // Extract frame at 1 second into segment
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -ss 1 -i \"{segmentPath}\" -vframes 1 -vf scale=160:90 \"{thumbPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();

        if (!File.Exists(thumbPath))
        {
            return null;
        }

        return thumbPath;
    }
}
