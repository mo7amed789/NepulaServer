namespace NebulaServer.Services.Core;

public sealed class RecoveryStateService
{
    private readonly object _lock = new();

    private RecoveryStatus _state = new();

    public RecoveryStatus Get()
    {
        lock (_lock)
        {
            return new RecoveryStatus
            {
                IsRunning = _state.IsRunning,
                IsComplete = _state.IsComplete,
                LastRunUtc = _state.LastRunUtc,
                LastError = _state.LastError,
                TotalUnfinished = _state.TotalUnfinished,
                RecoveredJobs = _state.RecoveredJobs,
                SkippedJobs = _state.SkippedJobs
            };
        }
    }

    public void Update(Action<RecoveryStatus> update)
    {
        lock (_lock)
        {
            update(_state);
        }
    }
}

public sealed class RecoveryStatus
{
    public bool IsRunning { get; set; }

    public bool IsComplete { get; set; }

    public int TotalUnfinished { get; set; }

    public int RecoveredJobs { get; set; }

    public int SkippedJobs { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public string? LastError { get; set; }
}
