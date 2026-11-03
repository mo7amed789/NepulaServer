using NebulaServer.Models.Jobs;

namespace NebulaServer.Services;

public sealed class FileTransferSession : IAsyncDisposable
{
    private bool _disposed;
    private readonly CompletionTrackingStream _trackingStream;

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
        _trackingStream = new CompletionTrackingStream(stream);
        Stream = _trackingStream;
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

    public bool WasFullyRead => _trackingStream.WasFullyRead;

    public StorageService.FileLockLease FileLockLease { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await Stream.DisposeAsync();
        await FileLockLease.DisposeAsync();
    }

    private sealed class CompletionTrackingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _expectedLength;
        private int _wasFullyRead;

        public CompletionTrackingStream(Stream inner)
        {
            _inner = inner;
            _expectedLength = inner.CanSeek ? inner.Length : -1;
            if (_expectedLength == 0)
            {
                _wasFullyRead = 1;
            }
        }

        public bool WasFullyRead => Volatile.Read(ref _wasFullyRead) == 1;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            TrackCompletion();
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _inner.Read(buffer);
            TrackCompletion();
            return bytesRead;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            TrackCompletion();
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            TrackCompletion();
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void TrackCompletion()
        {
            if (_expectedLength >= 0 && _inner.Position >= _expectedLength)
            {
                Volatile.Write(ref _wasFullyRead, 1);
            }
        }
    }
}
