using Microsoft.Data.Sqlite;
using NebulaServer.Helpers;
using NebulaServer.Models.Jobs;
using SQLitePCL;

namespace NebulaServer.Services.Jobs;

public sealed class JobStore
{
    private readonly object _lock = new();
    private readonly string _dbPath;

    public JobStore()
    {
        Console.WriteLine(_dbPath);
        Batteries_V2.Init();
        RuntimePaths.EnsureDirectories();
        _dbPath = Path.Combine(RuntimePaths.Data, "jobs.db");
        EnsureCreated();
    }

    public void Save(JobSnapshot job)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        lock (_lock)
        {
            using var conn = OpenConnection();
            conn.Open();

            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS Jobs (
                        Id TEXT PRIMARY KEY,
                        Url TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        Progress REAL NOT NULL,
                        OutputPath TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        RequestJson TEXT NOT NULL,
                        TransferJson TEXT NOT NULL DEFAULT ''
                    );
                    """;

                create.ExecuteNonQuery();
            }

            using var insert = conn.CreateCommand();
            insert.CommandText =
                """
                INSERT OR REPLACE INTO Jobs
                (Id, Url, Status, Progress, OutputPath, CreatedAt, RequestJson, TransferJson)
                VALUES ($id, $url, $status, $progress, $outputPath, $createdAt, $requestJson, $transferJson);
                """;

            insert.Parameters.AddWithValue("$id", job.Id);
            insert.Parameters.AddWithValue("$url", job.Url);
            insert.Parameters.AddWithValue("$status", job.Status);
            insert.Parameters.AddWithValue("$progress", job.Progress);
            insert.Parameters.AddWithValue("$outputPath", (object?)job.OutputPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$requestJson", job.RequestJson ?? string.Empty);
            insert.Parameters.AddWithValue("$transferJson", job.TransferJson ?? string.Empty);

            insert.ExecuteNonQuery();
        }
    }

    public List<JobSnapshot> GetUnfinished()
    {
        var result = new List<JobSnapshot>();

        lock (_lock)
        {
            using var conn = OpenConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT Id, Url, Status, Progress, OutputPath, CreatedAt, RequestJson, TransferJson
                FROM Jobs
                WHERE Status NOT IN ('COMPLETED', 'FAILED', 'CANCELED')
                ORDER BY CreatedAt ASC;
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JobSnapshot
                {
                    Id = reader.GetString(0),
                    Url = reader.GetString(1),
                    Status = reader.GetString(2),
                    Progress = reader.GetDouble(3),
                    OutputPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    RequestJson = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    TransferJson = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
                });
            }
        }

        return result;
    }

    private void EnsureCreated()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                    CREATE TABLE IF NOT EXISTS Jobs (
                        Id TEXT PRIMARY KEY,
                        Url TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        Progress REAL NOT NULL,
                        OutputPath TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        RequestJson TEXT NOT NULL,
                        TransferJson TEXT NOT NULL DEFAULT ''
                    );
                    """;

            cmd.ExecuteNonQuery();

            EnsureColumn(conn, "TransferJson", "TEXT NOT NULL DEFAULT ''");
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string columnName, string definition)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Jobs);";

        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE Jobs ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        return new SqliteConnection($"Data Source={_dbPath}");
    }
}
