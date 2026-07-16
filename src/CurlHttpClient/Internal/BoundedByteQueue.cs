using System.Buffers;

namespace CurlHttp.Internal;

/// <summary>
/// Bounded producer/consumer byte queue between the native transfer thread
/// (synchronous producer) and the response stream consumer (asynchronous
/// reader).
///
/// Design constraints (these are what make the streaming design deadlock-free):
///  - The producer blocks when the buffer is full — that block IS the
///    backpressure that pauses libcurl — but the wait is ALWAYS wakeable by
///    <see cref="Abort"/>. A consumer that stops reading and disposes the
///    stream must be able to unblock a producer stuck inside the native
///    write callback, because libcurl's progress callback cannot fire while
///    control is inside our callback.
///  - The consumer never blocks a thread: waits are TaskCompletionSource-based.
///  - Chunks live in ArrayPool buffers; the pool gets them back as soon as
///    the consumer copies them out (or the queue is drained on abort).
/// </summary>
internal sealed class BoundedByteQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Segment> _segments = new();
    private readonly int _capacity;
    private int _headOffset; // consumed bytes of the head segment
    private int _bufferedBytes;
    private bool _completed;
    private Exception? _error;
    private bool _aborted;
    private Exception? _abortReason;
    private TaskCompletionSource<bool>? _dataWaiter;
    private int _syncWaiters;
    private bool _disposed;

    private readonly struct Segment(byte[] buffer, int length)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Length = length;
    }

    public BoundedByteQueue(int capacityBytes)
    {
        _capacity = capacityBytes;
    }

    /// <summary>Blocking write from the native worker thread. Returns false
    /// when the transfer must abort (queue aborted by consumer/cancellation).</summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return !Volatile.Read(ref _aborted);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        var segment = new Segment(buffer, data.Length);

        lock (_sync)
        {
            while (true)
            {
                if (_aborted || _completed)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return false;
                }
                // Always accept at least one segment so a chunk larger than
                // the capacity cannot wedge the transfer.
                if (_bufferedBytes < _capacity || _segments.Count == 0)
                {
                    _segments.Enqueue(segment);
                    _bufferedBytes += segment.Length;
                    _dataWaiter?.TrySetResult(true);
                    _dataWaiter = null;
                    // Only pay the pulse when a synchronous reader is parked;
                    // the async path signals through _dataWaiter above.
                    if (_syncWaiters > 0)
                    {
                        Monitor.PulseAll(_sync);
                    }
                    return true;
                }
                // Woken by the consumer draining data or by Abort().
                Monitor.Wait(_sync);
            }
        }
    }

    /// <summary>Producer signals a clean end of body.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            _completed = true;
            _dataWaiter?.TrySetResult(true);
            _dataWaiter = null;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Producer signals the transfer failed. Buffered data remains
    /// readable; the error surfaces once the buffer is drained (matching how
    /// socket-based handlers surface mid-body failures).</summary>
    public void Fault(Exception error)
    {
        lock (_sync)
        {
            _error ??= error;
            _completed = true;
            _dataWaiter?.TrySetResult(true);
            _dataWaiter = null;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Consumer-side hard stop (stream disposed / caller cancelled).
    /// Wakes a blocked producer immediately and releases all buffered data.</summary>
    public void Abort(Exception? reason = null)
    {
        lock (_sync)
        {
            if (_aborted)
            {
                return;
            }
            _aborted = true;
            _abortReason = reason;
            DrainLocked();
            _dataWaiter?.TrySetResult(true);
            _dataWaiter = null;
            Monitor.PulseAll(_sync);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> waiter;
            lock (_sync)
            {
                if (_segments.Count > 0)
                {
                    return DequeueLocked(destination.Span);
                }
                if (_aborted)
                {
                    throw _abortReason ?? new ObjectDisposedException(
                        nameof(BoundedByteQueue), "The response stream was disposed or the request was cancelled.");
                }
                if (_completed)
                {
                    if (_error is not null)
                    {
                        throw _error;
                    }
                    return 0;
                }
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _dataWaiter = waiter;
            }

            await using (cancellationToken.UnsafeRegister(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), waiter))
            {
                await waiter.Task.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Synchronous blocking read for the sync HttpClient.Send path —
    /// copies directly into the caller's span, no temp buffer, no Task. Blocks
    /// the calling thread (never a transfer thread) until data, EOF, or abort.</summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }
        lock (_sync)
        {
            while (true)
            {
                if (_segments.Count > 0)
                {
                    return DequeueLocked(destination);
                }
                if (_aborted)
                {
                    throw _abortReason ?? new ObjectDisposedException(
                        nameof(BoundedByteQueue), "The response stream was disposed or the request was cancelled.");
                }
                if (_completed)
                {
                    if (_error is not null)
                    {
                        throw _error;
                    }
                    return 0;
                }
                _syncWaiters++;
                try
                {
                    Monitor.Wait(_sync);
                }
                finally
                {
                    _syncWaiters--;
                }
            }
        }
    }

    /// <summary>Bytes currently buffered (diagnostics/tests).</summary>
    public int BufferedBytes
    {
        get
        {
            lock (_sync)
            {
                return _bufferedBytes;
            }
        }
    }

    private int DequeueLocked(Span<byte> destination)
    {
        int written = 0;
        while (written < destination.Length && _segments.Count > 0)
        {
            Segment head = _segments.Peek();
            int available = head.Length - _headOffset;
            int toCopy = Math.Min(available, destination.Length - written);
            head.Buffer.AsSpan(_headOffset, toCopy).CopyTo(destination[written..]);
            written += toCopy;
            _bufferedBytes -= toCopy;

            if (toCopy == available)
            {
                _segments.Dequeue();
                _headOffset = 0;
                ArrayPool<byte>.Shared.Return(head.Buffer);
            }
            else
            {
                _headOffset += toCopy;
            }
        }
        Monitor.PulseAll(_sync); // wake a producer waiting for space
        return written;
    }

    private void DrainLocked()
    {
        while (_segments.Count > 0)
        {
            ArrayPool<byte>.Shared.Return(_segments.Dequeue().Buffer);
        }
        _headOffset = 0;
        _bufferedBytes = 0;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _aborted = true;
            DrainLocked();
            _dataWaiter?.TrySetResult(true);
            _dataWaiter = null;
            Monitor.PulseAll(_sync);
        }
    }
}
