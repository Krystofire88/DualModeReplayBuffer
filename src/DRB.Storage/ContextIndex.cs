using System.IO;
using DRB.Core;
using DRB.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DRB.Storage;

/// <summary>
/// SQLite-based index for Context Mode frames.
/// Stores path, timestamp, and pHash for efficient retrieval and cleanup.
/// </summary>
public sealed class ContextIndex : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public ContextIndex(string dbPath, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        _logger.LogInformation("ContextIndex DB path: '{Path}'", dbPath);

        // WAL mode for concurrent access
        using var wal = _connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();

        // Create table
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS context_frames (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT    NOT NULL,
                timestamp INTEGER NOT NULL,
                phash     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp 
                ON context_frames(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(ContextFrame frame)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO context_frames (path, timestamp, phash) VALUES ($p, $t, $h)";
        cmd.Parameters.AddWithValue("$p", frame.Path);
        cmd.Parameters.AddWithValue("$t", new DateTimeOffset(frame.Timestamp).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$h", (long)frame.PHash);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ContextFrame> GetRange(DateTime from, DateTime to)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT path, timestamp, phash FROM context_frames WHERE timestamp BETWEEN $from AND $to ORDER BY timestamp";
        cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeMilliseconds());

        var results = new List<ContextFrame>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).UtcDateTime;
            results.Add(new ContextFrame(reader.GetString(0), ts, (ulong)reader.GetInt64(2)));
        }
        return results;
    }

    public void DeleteBefore(DateTime cutoff)
    {
        // Get paths first so we can delete files
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT path FROM context_frames WHERE timestamp < $cutoff";
        select.Parameters.AddWithValue("$cutoff", new DateTimeOffset(cutoff).ToUnixTimeMilliseconds());

        var paths = new List<string>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }
        }

        // Delete DB rows
        using var del = _connection.CreateCommand();
        del.CommandText = "DELETE FROM context_frames WHERE timestamp < $cutoff";
        del.Parameters.AddWithValue("$cutoff", new DateTimeOffset(cutoff).ToUnixTimeMilliseconds());
        del.ExecuteNonQuery();

        // Delete files
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort file deletion
            }
        }
    }

    /// <summary>
    /// Reconciles DB entries with actual files on disk.
    /// Removes DB rows whose files no longer exist.
    /// </summary>
    public void ReconcileWithDisk()
    {
        // Get all paths from DB
        using var select = _connection.CreateCommand();
        select.CommandText = "SELECT id, path FROM context_frames ORDER BY timestamp ASC";

        var toDelete = new List<long>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string path = reader.GetString(1);
                if (!File.Exists(path))
                    toDelete.Add(id);
            }
        }

        if (toDelete.Count == 0)
        {
            _logger.LogInformation("ReconcileWithDisk: all {N} DB entries have files on disk.",
                CountFrames());
            return;
        }

        _logger.LogInformation("ReconcileWithDisk: removing {N} stale DB entries " +
            "with no file on disk.", toDelete.Count);

        using var del = _connection.CreateCommand();
        del.CommandText = "DELETE FROM context_frames WHERE id = $id";
        del.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in toDelete)
        {
            del.Parameters["$id"].Value = id;
            del.ExecuteNonQuery();
        }

        _logger.LogInformation("ReconcileWithDisk: done. DB now has {N} entries.",
            CountFrames());
    }

    private long CountFrames()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM context_frames";
        return (long)cmd.ExecuteScalar()!;
    }

    public void EnforceMaxFrames(int maxFrames)
    {
        // Count total rows
        using var count = _connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM context_frames";
        long total = (long)count.ExecuteScalar()!;

        _logger.LogDebug("EnforceMaxFrames: total={Total}, max={Max}", total, maxFrames);

        if (total <= maxFrames)
        {
            _logger.LogDebug("EnforceMaxFrames: under limit, no eviction needed.");
            return;
        }

        long toDelete = total - maxFrames;
        _logger.LogInformation("EnforceMaxFrames: evicting {N} frames", toDelete);

        // Get paths of oldest frames to delete files
        using var select = _connection.CreateCommand();
        select.CommandText =
            "SELECT path FROM context_frames ORDER BY timestamp ASC LIMIT $n";
        select.Parameters.AddWithValue("$n", toDelete);
        var paths = new List<string>();
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                paths.Add(reader.GetString(0));

        // Delete DB rows
        using var del = _connection.CreateCommand();
        del.CommandText =
            "DELETE FROM context_frames WHERE id IN " +
            "(SELECT id FROM context_frames ORDER BY timestamp ASC LIMIT $n)";
        del.Parameters.AddWithValue("$n", toDelete);
        int deleted = del.ExecuteNonQuery();

        // Delete files
        foreach (var path in paths)
        {
            bool exists = File.Exists(path);
            _logger.LogInformation("Evict: path='{Path}' exists={Exists}", path, exists);
            if (exists)
                File.Delete(path);
            else
                _logger.LogWarning("Evict: FILE NOT FOUND on disk: '{Path}'", path);
        }

        _logger.LogInformation("EnforceMaxFrames: eviction complete, " +
            "deleted {N} rows and files", paths.Count);
    }

    public void Dispose() => _connection.Dispose();
}
