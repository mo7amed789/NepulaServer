using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaServer.Helpers;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;
using NebulaServer.Services;
using NebulaServer.Services.Jobs;

RuntimePaths.Data = Path.Combine(Path.GetTempPath(), "NebulaQueueChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(RuntimePaths.Data);
Console.WriteLine($"Isolated test data: {RuntimePaths.Data}");
var checks = 0;
void Check(bool success, string message)
{
    if (!success) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

SQLitePCL.Batteries_V2.Init();
var path = Path.Combine(RuntimePaths.Data, "legacy.db");
using (var connection = new SqliteConnection($"Data Source={path}"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE Jobs (Id TEXT PRIMARY KEY, Url TEXT NOT NULL, Status TEXT NOT NULL,
            Progress REAL NOT NULL, OutputPath TEXT NULL, CreatedAt TEXT NOT NULL, RequestJson TEXT NOT NULL);
        INSERT INTO Jobs VALUES ('legacy', 'https://example.com', 'QUEUED', 12.5, NULL,
            '2026-01-01T00:00:00.0000000Z', '{}');
        """;
    command.ExecuteNonQuery();
}
var store = new JobStore(path);
var legacy = store.GetById("legacy")!;
Check(legacy.Progress == 12.5 && legacy.ThumbnailUrl == "" && legacy.TransferJson == "", "Legacy database migration preserves existing jobs");
legacy.Status = "COMPLETED";
legacy.Progress = 100;
legacy.TransferJson = "{\"items\":[]}";
store.Save(legacy);
var reopened = new JobStore(path);
Check(reopened.GetById("legacy")?.TransferJson == legacy.TransferJson && reopened.GetAllHistory().Count == 1,
    "UPSERT and reopen preserve fields without duplicate jobs");
Check(reopened.GetUnfinished().Count == 0 && reopened.GetById("missing") is null, "Recovery excludes completed jobs and missing ID returns null");

for (var i = 0; i < 500; i++)
    store.Save(new JobSnapshot { Id = "seed-" + i, Url = "https://example.com/" + i, Status = "QUEUED",
        CreatedAt = DateTime.UtcNow.AddSeconds(i), RequestJson = new string('x', 1024), TransferJson = "{}" });
Check(store.GetAllHistory()[0].Id == "seed-499" && store.GetUnfinished().Count == 500, "History sort and unfinished filtering are preserved");
store.GetById("seed-250");
store.GetAllHistory();
var scanWatch = Stopwatch.StartNew();
var scanBytes = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 100; i++) CheckLookup(store.GetAllHistory().FirstOrDefault(j => j.Id == "seed-250"));
scanBytes = GC.GetAllocatedBytesForCurrentThread() - scanBytes;
scanWatch.Stop();
var directWatch = Stopwatch.StartNew();
var directBytes = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 100; i++) CheckLookup(store.GetById("seed-250"));
directBytes = GC.GetAllocatedBytesForCurrentThread() - directBytes;
directWatch.Stop();
Console.WriteLine($"BENCHMARK 100 ID lookups / 501 rows: history scan {scanWatch.Elapsed.TotalMilliseconds:F1} ms / {scanBytes:N0} allocated bytes; indexed {directWatch.Elapsed.TotalMilliseconds:F1} ms / {directBytes:N0} allocated bytes");
static void CheckLookup(JobSnapshot? job)
{
    if (job?.Id != "seed-250") throw new InvalidOperationException("Lookup did not return expected job.");
}

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Performance:MaxConcurrentDownloads"] = "2" }).Build();
var python = new PythonProcessService();
var storage = new StorageService();
using var queue = new JobQueueManager(NullLogger<JobQueueManager>.Instance, config, python, storage,
    new NoopEvents(), store, new PlaylistEventBus());

var playlist = new DownloadJob { Request = new JobRequest { IsPlaylist = true } };
var mediaPath = Path.Combine(storage.GetPlaylistDirectoryPath(playlist.JobId), "01_test.mp4");
var bytes = Encoding.UTF8.GetBytes("deterministic media fixture");
File.WriteAllBytes(mediaPath, bytes);
var parse = typeof(JobQueueManager).GetMethod("ParseEngineMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)parse.Invoke(queue, new object[] { playlist, $"JOB_RESULT|{playlist.JobId}|{playlist.JobId}/01_test.mp4|Original title" })!;
var item = playlist.CompletedItems[1];
var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var expectedId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedHash + ":1"))).ToLowerInvariant();
Check(item.Sha256 == expectedHash && item.ItemId == expectedId && item.SizeBytes == bytes.Length && item.Title == "Original title",
    "Single-pass hashing preserves playlist SHA256, stable ID, title, and length");

var queuedCancel = queue.CreateJob(new JobRequest { Url = "https://example.com/cancel" });
Check(await queue.CancelJobAsync(queuedCancel.JobId), "Queued cancellation succeeds before workers start");
var activeJobs = Enumerable.Range(0, 4).Select(i => queue.CreateJob(new JobRequest { Url = "https://example.com/" + i })).ToArray();
await queue.StartAsync(CancellationToken.None);
try
{
    await UntilAsync(() => python.StartedProcessIds.Count == 2);
    Check(queue.GetStatistics().RunningJobs == 2 && queue.GetStatistics().QueuedJobs == 2,
        "Configured worker count bounds concurrent processes");
    Check(store.GetById(queuedCancel.JobId)?.Status == "CANCELED", "Canceled queued job is never started");
    Check(await queue.CancelJobAsync(activeJobs[0].JobId), "Running job cancellation succeeds");
    await UntilAsync(() => python.StartedProcessIds.Count == 3);
    Check(activeJobs[0].State == JobState.CANCELED, "Canceling one worker releases capacity for the next queued job");
    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await queue.StopAsync(stopTimeout.Token);
    Check(queue.ExecuteTask?.IsCompleted == true, "Hosted shutdown awaits every queue worker");
    Check(python.StartedProcessIds.All(id => !IsRunning(id)), "Shutdown leaves no started child process running");
    Check(activeJobs.Count(j => j.State == JobState.CANCELED) == 3 && activeJobs.Count(j => j.State == JobState.QUEUED) == 1,
        "Shutdown cancels active work and retains unstarted jobs for recovery");
}
finally
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await queue.StopAsync(timeout.Token);
    foreach (var id in python.StartedProcessIds)
    {
        try { using var child = Process.GetProcessById(id); child.Kill(entireProcessTree: true); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}

store.DeleteJob("legacy");
Check(store.GetById("legacy") is null, "Delete removes only the selected history entry");
store.ClearHistory();
Check(store.GetAllHistory().Count == 0, "Clear history removes all rows");
SqliteConnection.ClearAllPools();
Console.WriteLine($"Passed {checks} queue/store regression checks.");

static async Task UntilAsync(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (!predicate()) await Task.Delay(25, timeout.Token);
}
static bool IsRunning(int id)
{
    try { using var process = Process.GetProcessById(id); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
