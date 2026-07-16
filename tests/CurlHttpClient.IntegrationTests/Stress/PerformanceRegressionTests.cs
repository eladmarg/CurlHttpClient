using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CurlHttp.IntegrationTests.Stress;

/// <summary>
/// Performance regression gates. These assert allocation budgets, resource
/// bounds, and relative behavior — never fragile wall-clock thresholds — so
/// they are stable on shared CI. Budgets are set from measured post-optimization
/// numbers plus generous headroom; a regression (e.g. re-introducing a
/// per-read allocation) trips them.
/// </summary>
[Collection("integration")]
public class PerformanceRegressionTests(ServerFixture fixture, ITestOutputHelper output)
{
    private HttpClient Client => fixture.Client;

    /// <summary>Process-wide allocated bytes per op (GC.GetTotalAllocatedBytes
    /// counts all threads — the only reliable figure for async code whose
    /// continuations hop threads). Includes the in-process server's per-request
    /// allocation, so budgets carry headroom for that; the gate catches
    /// order-of-magnitude regressions (e.g. a re-introduced per-read array).</summary>
    private static async Task<long> AllocatedPerOpAsync(int iterations, Func<Task> op)
    {
        for (int i = 0; i < 50; i++)
        {
            await op();
        }
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < iterations; i++)
        {
            await op();
        }
        long after = GC.GetTotalAllocatedBytes(precise: true);
        return (after - before) / iterations;
    }

    [Fact]
    public async Task AsyncReusedGet_StaysWithinAllocationBudget()
    {
        long perOp = await AllocatedPerOpAsync(2000, async () =>
        {
            using HttpResponseMessage r = await Client.GetAsync(fixture.Http("/json"));
            await r.Content.CopyToAsync(Stream.Null);
        });
        output.WriteLine($"async reused GET: {perOp:N0} B/op (client + in-process server)");
        // Client + Kestrel per small request; budget catches a per-chunk or
        // per-read allocation regression (which would be 10s of KB/op).
        Assert.True(perOp < 32 * 1024, $"async reused GET allocated {perOp:N0} B/op (budget 32 KB)");
    }

    [Fact]
    public void SyncSendGet_StaysWithinAllocationBudget()
    {
        // The sync path was the big regression risk (was 264 KB/op before the
        // Read(Span) fix). Sync code stays on one thread, so per-thread
        // measurement is exact here.
        for (int i = 0; i < 50; i++)
        {
            SyncGet();
        }
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        const int iterations = 2000;
        for (int i = 0; i < iterations; i++)
        {
            SyncGet();
        }
        long perOp = (GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
        output.WriteLine($"sync Send GET: {perOp:N0} B/op");
        Assert.True(perOp < 48 * 1024, $"sync Send GET allocated {perOp:N0} B/op (budget 48 KB; was 264 KB/op on the client alone pre-fix)");

        void SyncGet()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json"));
            using HttpResponseMessage r = Client.Send(request);
            using Stream s = r.Content.ReadAsStream();
            s.CopyTo(Stream.Null);
        }
    }

    [Fact]
    public async Task StreamingDownload_DoesNotBufferTheWholeBody()
    {
        // The strongest streaming regression gate: total process allocation
        // for a large download must be a fraction of the body size. If the
        // handler ever buffered the whole body (e.g. ReadAsByteArrayAsync),
        // allocation would be >= the body. Both client and Kestrel stream with
        // pooled buffers, so the real figure is a few MB regardless of body.
        const int sizeBytes = 32 * 1024 * 1024;
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);

        using (HttpResponseMessage r = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http($"/large?bytes={sizeBytes}")),
            HttpCompletionOption.ResponseHeadersRead))
        {
            await using Stream s = await r.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[128 * 1024];
            while (await s.ReadAsync(buffer) > 0)
            {
            }
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        output.WriteLine($"32 MB download: {allocated:N0} B allocated ({allocated * 100.0 / sizeBytes:F0}% of body)");
        // Half the body is a generous ceiling that still fails a whole-body buffer.
        Assert.True(allocated < sizeBytes / 2,
            $"download allocated {allocated:N0} B for a {sizeBytes:N0} B body — body may be buffered whole");
    }

    [Fact]
    public async Task ConcurrentRequests_ThreadCount_StaysBounded()
    {
        int baseline = Process.GetCurrentProcess().Threads.Count;
        const int concurrency = 32;

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            for (int i = 0; i < 5; i++)
            {
                using HttpResponseMessage r = await Client.GetAsync(fixture.Http("/json"));
                await r.Content.CopyToAsync(Stream.Null);
            }
        }));

        int peak = Process.GetCurrentProcess().Threads.Count;
        output.WriteLine($"threads: baseline {baseline}, after {concurrency}-way concurrency {peak}");
        // The worker pool is bounded (default max(8, 2*cores)); the process
        // thread count must not grow unboundedly with request concurrency.
        Assert.True(peak - baseline < 128,
            $"thread count grew by {peak - baseline} under {concurrency}-way concurrency");
    }

    [Fact]
    public async Task SequentialRequests_ReuseConnection()
    {
        // Connection reuse is worth more than any micro-optimization; guard it.
        var ids = new HashSet<string>();
        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage r = await Client.GetAsync(fixture.Http("/json"));
            ids.Add(r.Headers.GetValues("X-Connection-Id").Single());
        }
        Assert.Single(ids);
    }

    [Fact]
    public async Task CancellationDuringDownload_CompletesWithinGenerousBound()
    {
        // Not a tight timing assertion — just that mid-transfer cancellation
        // (where the write callback observes the flag) is prompt, not ~1 s.
        using var cts = new CancellationTokenSource();
        using HttpResponseMessage r = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/large?bytes=104857600")),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using Stream s = await r.Content.ReadAsStreamAsync(cts.Token);
        byte[] buffer = new byte[4096];
        Assert.True(await s.ReadAsync(buffer, cts.Token) > 0);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await s.ReadAsync(buffer, cts.Token) > 0)
            {
            }
        });
        sw.Stop();
        output.WriteLine($"mid-download cancellation: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"mid-download cancellation took {sw.Elapsed}");
    }
}
