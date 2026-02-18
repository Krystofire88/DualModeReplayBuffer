using DRB.Core;
using DRB.Core.Models;
using Microsoft.Data.Sqlite;

namespace DRB.Storage;

public sealed class SqliteClipStorage : IClipStorage
{
    private readonly string _connectionString;

    public SqliteClipStorage()
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.IndexDatabasePath
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureFoldersExist();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Clips (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                DurationSeconds INTEGER NOT NULL,
                FilePath TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SaveEncodedFrameAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
    {
        // Stub: ring buffer eviction / backing storage not yet implemented.
        return Task.CompletedTask;
    }

    public Task SaveClipAsync(ClipRequest request, CancellationToken cancellationToken = default)
    {
        // Stub: materialize buffered frames into a clip and record row in SQLite.
        return Task.CompletedTask;
    }
}

