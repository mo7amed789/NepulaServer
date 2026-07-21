using NebulaServer.Models.Jobs;

namespace NebulaServer.Services;

public sealed class FileTransferSession : IAsyncDisposable
{
    private bool _disposed;

    public FileTransferSession(
        string sessionId,
        string jobId,
        int itemIndex,
        string filePath,
        string contentType,
        Stream stream,
        StorageService.FileLockLease fileLockLease)
    {
        SessionId = sessionId;
        JobId = jobId;
        ItemIndex = itemIndex;
        FilePath = filePath;
        ContentType = contentType;
        Stream = stream;
        FileLockLease = fileLockLease;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string SessionId { get; }

    public string JobId { get; }

    public int ItemIndex { get; }

    public string FilePath { get; }

    public string ContentType { get; }

    public DateTime CreatedAtUtc { get; }

    public Stream Stream { get; }

    public StorageService.FileLockLease FileLockLease { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await Stream.DisposeAsync();
        await FileLockLease.DisposeAsync();
    }
}
