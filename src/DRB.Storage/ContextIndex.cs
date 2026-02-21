using System.IO;
using DRB.Core;
using DRB.Core.Models;
using Microsoft.Data.Sqlite;

namespace DRB.Storage;

/// <summary>
/// SQLite-based index for Context Mode frames.
/// Stores path, timestamp, and pHash for efficient retrieval and cleanup.
/// </summary>
public sealed class ContextIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    public ContextIndex(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

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

    public void Dispose() => _connection.Dispose();
}
