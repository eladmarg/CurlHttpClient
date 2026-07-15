namespace CurlHttp.Internal;

/// <summary>
/// Read-only, forward-only view over the bounded body queue. Data is
/// delivered as the native transfer produces it; disposing the stream while
/// the transfer is still running aborts it (matching SocketsHttpHandler).
/// </summary>
internal sealed class CurlResponseStream : Stream
{
    private readonly CurlRequestContext _context;
    private bool _disposed;

    public CurlResponseStream(CurlRequestContext context)
    {
        _context = context;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _context.BodyQueue.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Synchronous reads block the caller's thread, never a transfer
        // thread; acceptable for compat with sync consumers.
        byte[] temp = new byte[buffer.Length];
        int read = _context.BodyQueue.ReadAsync(temp, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        temp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _context.OnResponseStreamDisposed();
            }
        }
        base.Dispose(disposing);
    }
}
