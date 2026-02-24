using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
            _logger.LogDebug("Evicted focus segment on recovery: {Path}", old);
        }

        if (recovered > 0)
            _logger.LogInformation("Recovered {Count} segments from disk in {Dir}.", recovered, _directory);
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

        string ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            _logger.LogWarning("FFmpeg not found for thumbnail extraction");
            return null;
        }

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
            _logger.LogWarning("Thumbnail extraction failed for: {Path}", segmentPath);
            return null;
        }

        return thumbPath;
    }

    private static string? FindFfmpeg()
    {
        // 1. Next to exe (use AppPaths for consistency)
        string exeDir = AppPaths.BaseDirectory;
        string candidate = Path.Combine(exeDir, "ffmpeg.exe");
        if (File.Exists(candidate)) return candidate;

        // 2. tools/ subfolder
        candidate = Path.Combine(exeDir, "tools", "ffmpeg.exe");
        if (File.Exists(candidate)) return candidate;

        // 3. PATH
        candidate = "ffmpeg";
        return candidate;
    }
}
