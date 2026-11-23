using System.Globalization;
using Microsoft.Data.Sqlite;
using NebulaServer.Helpers;
using NebulaServer.Models.Jobs;
using SQLitePCL;

namespace NebulaServer.Services.Jobs;

public sealed class JobStore
{
    private const string Columns = "Id, Url, Status, Progress, OutputPath, CreatedAt, RequestJson, TransferJson, ThumbnailUrl, Title";
    // Same projection without TransferJson: playlist transfer snapshots are
    // tens of KB per row and no history-list consumer reads them.
    private const string ColumnsNoTransfer = "Id, Url, Status, Progress, OutputPath, CreatedAt, RequestJson, ThumbnailUrl, Title";
    private readonly object _lock = new();
    private readonly string _connectionString;
    private long _historyRevision;

    // Incremented under the store lock on every mutation. Readers that merge
    // history with live job state (the desktop refresh timer) can skip a full
    // table reload while the revision is unchanged. Process-local by design:
    // the app is single-instance and the store is a DI singleton.
    public long HistoryRevision
    {
        get { lock (_lock) return _historyRevision; }
    }

    public JobStore() : this(Path.Combine(RuntimePaths.Data, "jobs.db")) { }

    // Explicit paths allow migrations and regression checks to use an isolated database.
    internal JobStore(string databasePath)
    {
        Batteries_V2.Init();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        EnsureCreated();
    }

    public void Save(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var command = conn.CreateCommand();
            // UPDATE in place: REPLACE deletes/reinserts the row and its index entries.
            command.CommandText = """
                INSERT INTO Jobs
                (Id, Url, Status, Progress, OutputPath, CreatedAt, RequestJson, TransferJson, ThumbnailUrl, Title)
                VALUES ($id, $url, $status, $progress, $outputPath, $createdAt, $requestJson, $transferJson, $thumbnailUrl, $title)
                ON CONFLICT(Id) DO UPDATE SET
                    Url = excluded.Url, Status = excluded.Status, Progress = excluded.Progress,
                    OutputPath = excluded.OutputPath, CreatedAt = excluded.CreatedAt,
                    RequestJson = excluded.RequestJson, TransferJson = excluded.TransferJson,
                    ThumbnailUrl = excluded.ThumbnailUrl, Title = excluded.Title;
                """;
            command.Parameters.AddWithValue("$id", job.Id);
            command.Parameters.AddWithValue("$url", job.Url);
            command.Parameters.AddWithValue("$status", job.Status);
            command.Parameters.AddWithValue("$progress", job.Progress);
            command.Parameters.AddWithValue("$outputPath", (object?)job.OutputPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$requestJson", job.RequestJson ?? string.Empty);
            command.Parameters.AddWithValue("$transferJson", job.TransferJson ?? string.Empty);
            command.Parameters.AddWithValue("$thumbnailUrl", job.ThumbnailUrl ?? string.Empty);
            command.Parameters.AddWithValue("$title", job.Title ?? string.Empty);
            command.ExecuteNonQuery();
            _historyRevision++;
        }
    }

    public JobSnapshot? GetById(string id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var command = conn.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM Jobs WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSnapshot(reader) : null;
        }
    }

    public List<JobSnapshot> GetUnfinished() => ReadJobs(
        "WHERE Status NOT IN ('COMPLETED', 'FAILED', 'CANCELED') ORDER BY CreatedAt ASC");

    public List<JobSnapshot> GetAllHistory() => ReadJobs("ORDER BY CreatedAt DESC");

    // History lists (web dashboard, desktop queue, mobile app) never render
    // per-item transfer manifests. Skipping the column keeps large playlist
    // snapshots out of these reads entirely.
    public List<JobSnapshot> GetHistoryWithoutTransfers() => ReadJobs("ORDER BY CreatedAt DESC", includeTransfer: false);

    private List<JobSnapshot> ReadJobs(string clause, bool includeTransfer = true)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var command = conn.CreateCommand();
            command.CommandText = $"SELECT {(includeTransfer ? Columns : ColumnsNoTransfer)} FROM Jobs {clause};";
            using var reader = command.ExecuteReader();
            var result = new List<JobSnapshot>();
            while (reader.Read()) result.Add(ReadSnapshot(reader, includeTransfer));
            return result;
        }
    }

    private static JobSnapshot ReadSnapshot(SqliteDataReader reader, bool includeTransfer = true) => new()
    {
        Id = reader.GetString(0),
        Url = reader.GetString(1),
        Status = reader.GetString(2),
        Progress = reader.GetDouble(3),
        OutputPath = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        RequestJson = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        TransferJson = includeTransfer && !reader.IsDBNull(7) ? reader.GetString(7) : string.Empty,
        ThumbnailUrl = reader.IsDBNull(includeTransfer ? 8 : 7) ? string.Empty : reader.GetString(includeTransfer ? 8 : 7),
        Title = reader.IsDBNull(includeTransfer ? 9 : 8) ? string.Empty : reader.GetString(includeTransfer ? 9 : 8)
    };

    public void DeleteJob(string id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM Jobs WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            _historyRevision++;
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var command = conn.CreateCommand();
            // History cleanup is intentionally limited to terminal jobs. Active and
            // queued jobs remain recoverable if the app closes during a download.
            command.CommandText = "DELETE FROM Jobs WHERE Status IN ('COMPLETED', 'FAILED', 'CANCELED');";
            command.ExecuteNonQuery();
            _historyRevision++;
        }
    }

    private void EnsureCreated()
    {
        using var conn = OpenConnection();
        using var command = conn.CreateCommand();
        // WAL avoids rewriting database pages at every commit. Keep FULL durability;
        // completed jobs must survive a power loss as well as a normal restart.
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Jobs (
                Id TEXT PRIMARY KEY,
                Url TEXT NOT NULL,
                Status TEXT NOT NULL,
                Progress REAL NOT NULL,
                OutputPath TEXT NULL,
                CreatedAt TEXT NOT NULL,
                RequestJson TEXT NOT NULL,
                TransferJson TEXT NOT NULL DEFAULT '',
                ThumbnailUrl TEXT NOT NULL DEFAULT ''
                ,Title TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_Jobs_CreatedAt ON Jobs(CreatedAt DESC);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(conn, "TransferJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "ThumbnailUrl", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(conn, "Title", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(SqliteConnection conn, string columnName, string definition)
    {
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Jobs);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
        }
        // Close the schema reader before migrating an older database.
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE Jobs ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
