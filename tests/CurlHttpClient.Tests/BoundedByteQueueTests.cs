using CurlHttp.Internal;
using Xunit;

namespace CurlHttp.Tests;

public class BoundedByteQueueTests
{
    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        using var queue = new BoundedByteQueue(1024);
        byte[] payload = [1, 2, 3, 4, 5];
        Assert.True(queue.Write(payload));
        queue.Complete();

        byte[] buffer = new byte[16];
        int read = await queue.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(5, read);
        Assert.Equal(payload, buffer[..5]);
        Assert.Equal(0, await queue.ReadAsync(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task PartialReads_PreserveOrderAcrossSegments()
    {
        using var queue = new BoundedByteQueue(1024);
        queue.Write(new byte[] { 1, 2, 3 });
        queue.Write(new byte[] { 4, 5, 6 });
        queue.Complete();

        byte[] two = new byte[2];
        Assert.Equal(2, await queue.ReadAsync(two, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2 }, two);

        byte[] rest = new byte[10];
        Assert.Equal(4, await queue.ReadAsync(rest, CancellationToken.None));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, rest[..4]);
    }

    [Fact]
    public async Task FullBuffer_BlocksProducer_UntilConsumerDrains()
    {
        using var queue = new BoundedByteQueue(64 * 1024);
        // Fill beyond capacity: first write accepted, second write must block.
        Assert.True(queue.Write(new byte[64 * 1024]));

        var secondWrite = Task.Run(() => queue.Write(new byte[1024]));
        await Task.Delay(100);
        Assert.False(secondWrite.IsCompleted);

        byte[] drain = new byte[64 * 1024];
        int drained = 0;
        while (drained < 64 * 1024)
        {
            drained += await queue.ReadAsync(drain, CancellationToken.None);
        }
        Assert.True(await secondWrite.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Abort_WakesBlockedProducer()
    {
        using var queue = new BoundedByteQueue(1024);
        Assert.True(queue.Write(new byte[1024]));
        var blockedWrite = Task.Run(() => queue.Write(new byte[1024]));
        await Task.Delay(100);
        Assert.False(blockedWrite.IsCompleted);

        queue.Abort(new OperationCanceledException());
        // The producer must observe the abort and report failure to libcurl.
        Assert.False(await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Abort_SurfacesReasonToConsumer()
    {
        using var queue = new BoundedByteQueue(1024);
        var readTask = queue.ReadAsync(new byte[8], CancellationToken.None).AsTask();
        queue.Abort(new TaskCanceledException("cancelled by test"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => readTask);
    }

    [Fact]
    public async Task Fault_DeliversBufferedDataBeforeError()
    {
        using var queue = new BoundedByteQueue(1024);
        queue.Write(new byte[] { 9, 9 });
        queue.Fault(new IOException("boom"));

        byte[] buffer = new byte[8];
        Assert.Equal(2, await queue.ReadAsync(buffer, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(
            async () => await queue.ReadAsync(buffer, CancellationToken.None));
    }

    [Fact]
    public async Task PendingRead_IsWokenByWrite()
    {
        using var queue = new BoundedByteQueue(1024);
        byte[] buffer = new byte[8];
        var readTask = queue.ReadAsync(buffer, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(readTask.IsCompleted);

        queue.Write(new byte[] { 7 });
        Assert.Equal(1, await readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, buffer[0]);
    }

    [Fact]
    public async Task PendingRead_HonorsCancellationToken()
    {
        using var queue = new BoundedByteQueue(1024);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queue.ReadAsync(new byte[8], cts.Token));
    }

    [Fact]
    public async Task ConcurrentProducerConsumer_TransfersEverything()
    {
        using var queue = new BoundedByteQueue(32 * 1024);
        const int total = 4 * 1024 * 1024;
        var producer = Task.Run(() =>
        {
            var random = new Random(42);
            byte[] chunk = new byte[7919];
            int produced = 0;
            while (produced < total)
            {
                int size = Math.Min(chunk.Length, total - produced);
                random.NextBytes(chunk.AsSpan(0, size));
                if (!queue.Write(chunk.AsSpan(0, size)))
                {
                    return produced;
                }
                produced += size;
            }
            queue.Complete();
            return produced;
        });

        byte[] buffer = new byte[13007];
        long consumed = 0;
        int read;
        while ((read = await queue.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            consumed += read;
        }
        Assert.Equal(total, await producer);
        Assert.Equal(total, consumed);
    }

    [Fact]
    public void OversizedSingleChunk_IsAcceptedToAvoidWedging()
    {
        using var queue = new BoundedByteQueue(1024);
        Assert.True(queue.Write(new byte[8192])); // larger than capacity
    }

    /* ---- rendezvous fast-path race tests (P3) ---- */

    [Fact]
    public async Task Rendezvous_ParkedReader_IsFilledDirectlyByProducer()
    {
        using var queue = new BoundedByteQueue(64 * 1024);
        byte[] dest = new byte[16];

        // Park the reader first (queue empty), then produce.
        ValueTask<int> read = queue.ReadAsync(dest, CancellationToken.None);
        Assert.False(read.IsCompleted); // parked, awaiting rendezvous
        await Task.Delay(20);

        Assert.True(queue.Write([1, 2, 3, 4, 5]));
        int n = await read;
        Assert.Equal(5, n);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, dest[..5]);
        // Nothing left buffered — the bytes went straight to the reader.
        Assert.Equal(0, queue.BufferedBytes);
    }

    [Fact]
    public async Task Rendezvous_ReaderBufferSmallerThanChunk_QueuesRemainderInOrder()
    {
        using var queue = new BoundedByteQueue(64 * 1024);
        byte[] small = new byte[3];
        ValueTask<int> read = queue.ReadAsync(small, CancellationToken.None);
        await Task.Delay(20);

        Assert.True(queue.Write([10, 11, 12, 13, 14])); // 5 bytes, reader wants 3
        Assert.Equal(3, await read);
        Assert.Equal(new byte[] { 10, 11, 12 }, small);

        // Remainder is queued in order.
        byte[] rest = new byte[8];
        Assert.Equal(2, await queue.ReadAsync(rest, CancellationToken.None));
        Assert.Equal(new byte[] { 13, 14 }, rest[..2]);
    }

    [Fact]
    public async Task Rendezvous_CancellationBeforeFill_CancelsTheParkedRead()
    {
        using var queue = new BoundedByteQueue(1024);
        using var cts = new CancellationTokenSource();
        ValueTask<int> read = queue.ReadAsync(new byte[16], cts.Token);
        await Task.Delay(20);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        // A subsequent producer write does not corrupt state (no parked reader).
        Assert.True(queue.Write([1, 2, 3]));
        Assert.Equal(3, queue.BufferedBytes);
    }

    [Fact]
    public async Task Rendezvous_ProducerWinsCancellationRace_ReturnsBytesNotCancelled()
    {
        // Fire cancellation and the fill "simultaneously"; if the producer
        // copied the bytes first, the read must return them (never cancelled
        // away). Repeat to shake the race.
        for (int i = 0; i < 200; i++)
        {
            using var queue = new BoundedByteQueue(1024);
            using var cts = new CancellationTokenSource();
            byte[] dest = new byte[4];
            ValueTask<int> read = queue.ReadAsync(dest, cts.Token);

            var barrier = new Barrier(2);
            Task producer = Task.Run(() =>
            {
                barrier.SignalAndWait();
                queue.Write([9, 9, 9, 9]);
            });
            Task canceller = Task.Run(() =>
            {
                barrier.SignalAndWait();
                cts.Cancel();
            });

            int result;
            try
            {
                result = await read;
            }
            catch (OperationCanceledException)
            {
                await Task.WhenAll(producer, canceller);
                continue; // cancellation won — valid outcome
            }
            // Producer won: the full 4 bytes must be present.
            Assert.Equal(4, result);
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, dest);
            await Task.WhenAll(producer, canceller);
        }
    }

    [Fact]
    public async Task Rendezvous_AbortWithParkedReader_SurfacesReason()
    {
        using var queue = new BoundedByteQueue(1024);
        ValueTask<int> read = queue.ReadAsync(new byte[16], CancellationToken.None);
        await Task.Delay(20);

        queue.Abort(new TaskCanceledException("aborted with parked reader"));
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await read);
    }

    [Fact]
    public async Task Rendezvous_CompleteWithParkedReader_ReturnsEof()
    {
        using var queue = new BoundedByteQueue(1024);
        ValueTask<int> read = queue.ReadAsync(new byte[16], CancellationToken.None);
        await Task.Delay(20);

        queue.Complete();
        Assert.Equal(0, await read); // EOF
    }

    [Fact]
    public async Task Rendezvous_FaultWithParkedReader_ThrowsError()
    {
        using var queue = new BoundedByteQueue(1024);
        ValueTask<int> read = queue.ReadAsync(new byte[16], CancellationToken.None);
        await Task.Delay(20);

        queue.Fault(new IOException("mid-body failure"));
        await Assert.ThrowsAsync<IOException>(async () => await read);
    }

    [Fact]
    public async Task Rendezvous_And_QueuedData_NeverReorder_UnderConcurrency()
    {
        // Producer and consumer alternate between rendezvous (reader parked)
        // and queued (reader briefly behind) — the byte sequence must be exact.
        using var queue = new BoundedByteQueue(8 * 1024);
        const int total = 2 * 1024 * 1024;

        Task producer = Task.Run(() =>
        {
            var random = new Random(99);
            byte[] chunk = new byte[4096];
            int produced = 0;
            while (produced < total)
            {
                int size = Math.Min(chunk.Length, total - produced);
                // Deterministic byte at each absolute position.
                for (int i = 0; i < size; i++)
                {
                    chunk[i] = (byte)((produced + i) * 31 + 7);
                }
                if (!queue.Write(chunk.AsSpan(0, size)))
                {
                    return;
                }
                produced += size;
                if (random.Next(3) == 0)
                {
                    Thread.Yield(); // let the consumer park (rendezvous path)
                }
            }
            queue.Complete();
        });

        byte[] buffer = new byte[3000];
        long position = 0;
        int read;
        while ((read = await queue.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                Assert.Equal((byte)((position + i) * 31 + 7), buffer[i]);
            }
            position += read;
        }
        Assert.Equal(total, position);
        await producer;
    }
}
