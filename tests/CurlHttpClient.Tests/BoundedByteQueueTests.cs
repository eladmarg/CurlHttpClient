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
}
