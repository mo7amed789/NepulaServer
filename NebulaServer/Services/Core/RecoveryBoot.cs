using System.Text.Json;
using NebulaServer.Models;
using NebulaServer.Models.Jobs;
using NebulaServer.Services.Jobs;
using NebulaServer.Services;

namespace NebulaServer.Services.Core;

public sealed class RecoveryBoot
{
    private readonly JobStore _store;
    private readonly JobQueueManager _queue;
    private readonly StreamingTransferService _streamingTransfers;
    private readonly RecoveryStateService _state;

    public RecoveryBoot(JobStore store, JobQueueManager queue, StreamingTransferService streamingTransfers, RecoveryStateService state)
    {
        _store = store;
        _queue = queue;
        _streamingTransfers = streamingTransfers;
        _state = state;
    }

    public Task RecoverAsync()
    {
        _state.Update(state =>
        {
            state.IsRunning = true;
            state.IsComplete = false;
            state.LastError = null;
            state.LastRunUtc = DateTime.UtcNow;
            state.RecoveredJobs = 0;
            state.SkippedJobs = 0;
        });

        try
        {
            var unfinished = _store.GetUnfinished();

            _state.Update(state => state.TotalUnfinished = unfinished.Count);

            foreach (var snapshot in unfinished)
            {
                var request = TryDeserializeRequest(snapshot);

                if (request is null)
                {
                    _state.Update(state => state.SkippedJobs++);
                    continue;
                }

                Console.WriteLine($"Recovering job {snapshot.Id}");

                var job = new DownloadJob
                {
                    JobId = snapshot.Id,
                    Request = request,
                    State = JobState.QUEUED,
                    ProgressPercentage = "0%",
                    Speed = string.Empty,
                    Eta = string.Empty,
                    OutputFileName = snapshot.OutputPath is null ? null : Path.GetFileName(snapshot.OutputPath),
                    Message = "Recovered after restart",
                    CreatedAt = snapshot.CreatedAt
                };

                _queue.RestoreJob(job);

                var transferSnapshot = TryDeserializeTransfer(snapshot);
                if (transferSnapshot is not null)
                {
                    _queue.RestoreTransferState(job, transferSnapshot);
                    _streamingTransfers.RestoreSession(job.JobId, job.CompletedItems.Values);
                }

                _state.Update(state => state.RecoveredJobs++);
            }

            _state.Update(state =>
            {
                state.IsRunning = false;
                state.IsComplete = true;
                state.LastError = null;
                state.LastRunUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex)
        {
            _state.Update(state =>
            {
                state.IsRunning = false;
                state.IsComplete = false;
                state.LastError = ex.Message;
                state.LastRunUtc = DateTime.UtcNow;
            });
            throw;
        }

        return Task.CompletedTask;
    }

    private static JobRequest? TryDeserializeRequest(JobSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.RequestJson))
        {
            try
            {
                return JsonSerializer.Deserialize<JobRequest>(snapshot.RequestJson);
            }
            catch
            {
                // Fall through to URL-only fallback.
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.Url))
            return null;

        return new JobRequest
        {
            Url = snapshot.Url
        };
    }

    private static TransferSnapshot? TryDeserializeTransfer(JobSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TransferJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TransferSnapshot>(snapshot.TransferJson);
        }
        catch
        {
            return null;
        }
    }
}
