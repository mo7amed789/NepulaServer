using System.Collections.Concurrent;

namespace NebulaServer.Models.Jobs;

public sealed class DownloadSession
{
    private readonly ConcurrentDictionary<int, PlaylistItemResult> _deliveredItems = new();
    private readonly ConcurrentDictionary<int, PlaylistItemResult> _inFlightItems = new();

    public DownloadSession(string jobId, string? deviceId = null)
    {
        SessionId = string.IsNullOrWhiteSpace(deviceId)
            ? jobId
            : $"{jobId}:{deviceId}";

        JobId = jobId;
        DeviceId = deviceId;
        CreatedAtUtc = DateTime.UtcNow;
        LastAccessAtUtc = CreatedAtUtc;
    }

    public string SessionId { get; }

    public string JobId { get; }

    public string? DeviceId { get; }

    public DateTime CreatedAtUtc { get; }

    public DateTime LastAccessAtUtc { get; private set; }

    public TransferState State { get; private set; } = TransferState.Queued;

    public IReadOnlyCollection<PlaylistItemResult> DeliveredItems => _deliveredItems.Values
        .OrderBy(item => item.Index)
        .ToArray();

    public void RestoreFromItems(IEnumerable<PlaylistItemResult> items)
    {
        _deliveredItems.Clear();
        _inFlightItems.Clear();

        foreach (var item in items)
        {
            if (item.State == TransferState.Completed)
            {
                _deliveredItems[item.Index] = item;
            }
            else if (item.State == TransferState.Transferring)
            {
                _inFlightItems[item.Index] = item;
            }
        }

        State = _inFlightItems.IsEmpty
            ? (_deliveredItems.IsEmpty ? TransferState.Queued : TransferState.Completed)
            : TransferState.Transferring;
    }

    public bool IsDelivered(int index) => _deliveredItems.ContainsKey(index);

    public bool IsInFlight(int index) => _inFlightItems.ContainsKey(index);

    public bool TryReserve(PlaylistItemResult item)
    {
        if (_deliveredItems.ContainsKey(item.Index))
            return false;

        var added = _inFlightItems.TryAdd(item.Index, item);
        if (added)
        {
            State = TransferState.Transferring;
            Touch();
        }

        return added;
    }

    public void MarkCompleted(PlaylistItemResult item)
    {
        _inFlightItems.TryRemove(item.Index, out _);
        _deliveredItems[item.Index] = item;
        State = TransferState.Completed;
        Touch();
    }

    public void Release(PlaylistItemResult item)
    {
        _inFlightItems.TryRemove(item.Index, out _);
        if (_deliveredItems.IsEmpty)
        {
            State = TransferState.Ready;
        }

        Touch();
    }

    public void MarkFailed()
    {
        State = TransferState.Failed;
        Touch();
    }

    public void Touch()
    {
        LastAccessAtUtc = DateTime.UtcNow;
    }
}
