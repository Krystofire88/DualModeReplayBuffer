using System;
using System.IO;
using DRB.Core;
using DRB.Core.Models;
using Microsoft.Data.Sqlite;

namespace DRB.Storage;

/// <summary>
/// SQLite-based index for Context Mode frames.
/// Stores path, timestamp, pHash, and foreground app info for efficient retrieval and cleanup.
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

        // Create table with app info columns
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS context_frames (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp INTEGER NOT NULL,
                path      TEXT    NOT NULL,
                phash     INTEGER NOT NULL,
                app_name  TEXT    NOT NULL DEFAULT '',
                app_path  TEXT    NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_timestamp 
                ON context_frames(timestamp);
            """;
        cmd.ExecuteNonQuery();

        // Run migration to add app_name and app_path columns if they don't exist
        RunMigration();
    }

    /// <summary>
    /// Migrate existing database to add app_name and app_path columns if missing.
    /// </summary>
    private void RunMigration()
    {
        try
        {
            // Check if app_name column exists
            using var check = _connection.CreateCommand();
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='app_name'";
            long hasAppName = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasAppName == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN app_name TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }

            // Check if app_path column exists
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='app_path'";
            long hasAppPath = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasAppPath == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN app_path TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }
            
            // Check if window_title column exists
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='window_title'";
            long hasWindowTitle = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasWindowTitle == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN window_title TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }
            
            // Check if file_path column exists
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='file_path'";
            long hasFilePath = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasFilePath == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN file_path TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }
            
            // Check if url column exists
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='url'";
            long hasUrl = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasUrl == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN url TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }
            
            // Check if ocr_text column exists
            check.CommandText = 
                "SELECT COUNT(*) FROM pragma_table_info('context_frames') " +
                "WHERE name='ocr_text'";
            long hasOcrText = (long)(check.ExecuteScalar() ?? 0);
            
            if (hasOcrText == 0)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = "ALTER TABLE context_frames ADD COLUMN ocr_text TEXT DEFAULT ''";
                addCol.ExecuteNonQuery();
            }
        }
        catch (Exception)
        {
        }
    }

    public void Insert(ContextFrame frame)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO context_frames (path, timestamp, phash, app_name, app_path, window_title, file_path, url) VALUES ($p, $t, $h, $an, $ap, $wt, $fp, $url)";
        cmd.Parameters.AddWithValue("$p", frame.Path);
        cmd.Parameters.AddWithValue("$t", new DateTimeOffset(frame.Timestamp).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$h", (long)frame.PHash);
        cmd.Parameters.AddWithValue("$an", frame.AppName ?? "");
        cmd.Parameters.AddWithValue("$ap", frame.AppPath ?? "");
        cmd.Parameters.AddWithValue("$wt", frame.WindowTitle ?? "");
        cmd.Parameters.AddWithValue("$fp", frame.FilePath ?? "");
        cmd.Parameters.AddWithValue("$url", frame.Url ?? "");
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ContextFrame> GetRange(DateTime from, DateTime to)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, path, timestamp, phash, app_name, app_path, window_title, file_path, url, ocr_text FROM context_frames WHERE timestamp BETWEEN $from AND $to ORDER BY timestamp";
        cmd.Parameters.AddWithValue("$from", new DateTimeOffset(from).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", new DateTimeOffset(to).ToUnixTimeMilliseconds());

        var results = new List<ContextFrame>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFrame(reader));
        }
        return results;
    }

    /// <summary>
    /// Gets all context frames ordered by timestamp ascending.
    /// </summary>
    public List<ContextFrame> GetAllFrames()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, path, timestamp, phash, app_name, app_path, window_title, file_path, url, ocr_text FROM context_frames ORDER BY timestamp ASC";

        var results = new List<ContextFrame>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFrame(reader));
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
            return;
        }

        using var del = _connection.CreateCommand();
        del.CommandText = "DELETE FROM context_frames WHERE id = $id";
        del.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var id in toDelete)
        {
            del.Parameters["$id"].Value = id;
            del.ExecuteNonQuery();
        }
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

        if (total <= maxFrames)
        {
            return;
        }

        long toDelete = total - maxFrames;

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
            if (exists)
                File.Delete(path);
        }
    }

    /// <summary>
    /// Clears all frames from the database.
    /// </summary>
    public void ClearAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM context_frames";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the OCR text for a frame identified by path.
    /// </summary>
    public void UpdateOcrText(string path, string ocrText)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = 
            "UPDATE context_frames SET ocr_text=$text WHERE path=$path";
        cmd.Parameters.AddWithValue("$text", ocrText);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Searches for frames matching the given text query.
    /// Case-insensitive LIKE search across ocr_text and window_title.
    /// </summary>
    public List<ContextFrame> SearchByText(string query)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = 
            "SELECT id, timestamp, path, app_name, app_path, " +
            "window_title, file_path, url, ocr_text " +
            "FROM context_frames " +
            "WHERE ocr_text LIKE $q OR window_title LIKE $q " +
            "ORDER BY timestamp DESC " +
            "LIMIT 200";
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        
        var results = new List<ContextFrame>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadFrame(reader));
        return results;
    }

    /// <summary>
    /// Helper method to read a ContextFrame from the current reader position.
    /// </summary>
    /// Query columns: id(0), path(1), timestamp(2), phash(3), app_name(4), app_path(5), window_title(6), file_path(7), url(8), ocr_text(9)
    private ContextFrame ReadFrame(SqliteDataReader r) => new(
        r.GetString(1),  // path
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).LocalDateTime,  // timestamp
        (ulong)r.GetInt64(3),  // phash
        r.GetString(4),  // app_name
        r.GetString(5),  // app_path
        r.GetString(6),  // window_title
        r.GetString(7),  // file_path
        r.IsDBNull(8) ? "" : r.GetString(8),  // url
        r.IsDBNull(9) ? "" : r.GetString(9));  // ocr_text

    public void Dispose() => _connection.Dispose();
}
