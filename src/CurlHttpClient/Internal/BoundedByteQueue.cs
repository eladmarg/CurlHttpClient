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
///  - The consumer never blocks a thread on the async path: waits are
///    TaskCompletionSource-based.
///
/// Rendezvous fast path: when a reader is already parked with a destination
/// buffer and no data is queued ahead of it, the producer copies straight
/// from the native buffer into the reader's destination and completes the
/// read — the single copy in the steady streaming state, with no pooled
/// intermediate segment. Invariants that keep it correct:
///  - A parked reader implies an empty segment queue (the producer never
///    enqueues while a reader waits), so rendezvous bytes can never jump
///    queued bytes.
///  - There is at most one parked reader (single-consumer contract).
///  - The parked-reader slot is claimed with one atomic transition under the
///    lock; producer, cancellation, Complete/Fault/Abort race to claim it and
///    only the claimant completes the TCS — so bytes are never "cancelled
///    away" after they were copied.
///  - Completion uses RunContinuationsAsynchronously, so completing the TCS
///    under the lock never runs the consumer's continuation inline on the
///    native callback thread.
/// Chunks that must be buffered (slow consumer / no parked reader) live in
/// ArrayPool segments, returned as soon as the consumer copies them out or
/// the queue is drained on abort.
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

    // The single parked async reader, if any (rendezvous target). A parked
    // reader implies _segments is empty.
    private TaskCompletionSource<int>? _pendingReadTcs;
    private Memory<byte> _pendingReadDest;

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

        lock (_sync)
        {
            while (true)
            {
                if (_aborted || _completed)
                {
                    return false;
                }

                // Rendezvous: hand the bytes straight to a parked reader.
                if (_pendingReadTcs is not null)
                {
                    Span<byte> dest = _pendingReadDest.Span;
                    int n = Math.Min(dest.Length, data.Length);
                    data[..n].CopyTo(dest);
                    TaskCompletionSource<int> reader = _pendingReadTcs;
                    _pendingReadTcs = null;
                    _pendingReadDest = default;
                    // Reader buffer smaller than this chunk: queue the tail so
                    // the reader's next call picks it up (order preserved).
                    if (n < data.Length)
                    {
                        EnqueueLocked(data[n..]);
                    }
                    // RunContinuationsAsynchronously → continuation is queued to
                    // the pool, never run inline under this lock.
                    reader.SetResult(n);
                    return true;
                }

                // No parked reader: buffer with backpressure. Always accept at
                // least one segment so a chunk larger than the capacity cannot
                // wedge the transfer.
                if (_bufferedBytes < _capacity || _segments.Count == 0)
                {
                    EnqueueLocked(data);
                    if (_syncWaiters > 0)
                    {
                        Monitor.PulseAll(_sync);
                    }
                    return true;
                }

                // Buffer full and no reader draining: block (backpressure).
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
            ClaimPendingReaderLocked()?.SetResult(0); // EOF
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Producer signals the transfer failed. Buffered data remains
    /// readable; the error surfaces once the buffer is drained (matching how
    /// socket-based handlers surface mid-body failures). A parked reader (which
    /// implies an empty buffer) receives the error immediately.</summary>
    public void Fault(Exception error)
    {
        lock (_sync)
        {
            _error ??= error;
            _completed = true;
            ClaimPendingReaderLocked()?.SetException(error);
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
            ClaimPendingReaderLocked()?.SetException(reason ?? AbortException());
            Monitor.PulseAll(_sync);
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return new ValueTask<int>(0);
        }

        TaskCompletionSource<int> tcs;
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }
            if (_segments.Count > 0)
            {
                return new ValueTask<int>(DequeueLocked(destination.Span));
            }
            if (_aborted)
            {
                return ValueTask.FromException<int>(_abortReason ?? AbortException());
            }
            if (_completed)
            {
                return _error is not null
                    ? ValueTask.FromException<int>(_error)
                    : new ValueTask<int>(0);
            }
            // Park as the single pending reader; the next Write rendezvous-fills.
            tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReadTcs = tcs;
            _pendingReadDest = destination;
        }
        return AwaitPendingReadAsync(tcs, cancellationToken);
    }

    private async ValueTask<int> AwaitPendingReadAsync(
        TaskCompletionSource<int> tcs, CancellationToken cancellationToken)
    {
        // Cancellation claims the parked slot (only if THIS read is still the
        // parked one), so a producer that already filled the buffer wins and
        // the copied bytes are returned rather than cancelled away.
        await using (cancellationToken.UnsafeRegister(static (state, token) =>
        {
            var (queue, reader) = ((BoundedByteQueue, TaskCompletionSource<int>))state!;
            bool claimed;
            lock (queue._sync)
            {
                claimed = ReferenceEquals(queue._pendingReadTcs, reader);
                if (claimed)
                {
                    queue._pendingReadTcs = null;
                    queue._pendingReadDest = default;
                }
            }
            if (claimed)
            {
                reader.TrySetCanceled(token);
            }
        }, (this, tcs)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous blocking read for the sync HttpClient.Send path —
    /// copies directly into the caller's span, no temp buffer, no Task. Blocks
    /// the calling thread (never a transfer thread) until data, EOF, or abort.
    /// Uses the segment queue (not the rendezvous path, which requires a
    /// Memory that cannot be formed from a Span).</summary>
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
                    throw _abortReason ?? AbortException();
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

    private TaskCompletionSource<int>? ClaimPendingReaderLocked()
    {
        TaskCompletionSource<int>? tcs = _pendingReadTcs;
        _pendingReadTcs = null;
        _pendingReadDest = default;
        return tcs;
    }

    private void EnqueueLocked(ReadOnlySpan<byte> data)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        _segments.Enqueue(new Segment(buffer, data.Length));
        _bufferedBytes += data.Length;
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

    private static ObjectDisposedException AbortException()
        => new(nameof(BoundedByteQueue),
            "The response stream was disposed or the request was cancelled.");

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
            ClaimPendingReaderLocked()?.SetException(_abortReason ?? AbortException());
            Monitor.PulseAll(_sync);
        }
    }
}
