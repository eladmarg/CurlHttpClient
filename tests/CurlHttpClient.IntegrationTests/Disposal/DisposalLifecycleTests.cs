using System.Diagnostics;
using System.Net;
using Xunit;

namespace CurlHttp.IntegrationTests.Disposal;

/// <summary>Handler/response lifecycle: disposal in every phase, double
/// disposal, disposal races, and post-disposal behavior.</summary>
[Collection("integration")]
public class DisposalLifecycleTests(ServerFixture fixture)
{
    private CurlHttpMessageHandler NewHandler() => new(fixture.BaseOptions);

    [Fact]
    public void DisposeUnusedHandler_IsInstant()
    {
        var handler = NewHandler();
        var stopwatch = Stopwatch.StartNew();
        handler.Dispose();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void DisposeHandlerTwice_AndConcurrently_IsSafe()
    {
        var handler = NewHandler();
        Parallel.For(0, 8, _ => handler.Dispose());
        handler.Dispose();
    }

    [Fact]
    public async Task RequestAfterDispose_ThrowsObjectDisposed()
    {
        var handler = NewHandler();
        using (var client = new HttpClient(handler, disposeHandler: false))
        {
            using HttpResponseMessage warm = await client.GetAsync(fixture.Http("/json"));
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }
        handler.Dispose();

        using var client2 = new HttpClient(handler, disposeHandler: false);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client2.GetAsync(fixture.Http("/json")));
    }

    [Fact]
    public async Task DisposeHandler_WithActiveRequest_UnblocksItPromptly()
    {
        var handler = NewHandler();
        var client = new HttpClient(handler, disposeHandler: false);
        Task<HttpResponseMessage> pending =
            client.GetAsync(fixture.Http("/delayed-headers?ms=30000"));
        await Task.Delay(300); // let it reach the native transfer

        var stopwatch = Stopwatch.StartNew();
        handler.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12),
            $"disposal with an active request took {stopwatch.Elapsed}");
        client.Dispose();
    }

    [Fact]
    public async Task DisposeHandler_WhileResponseBodyIsStreaming_AbortsTheStream()
    {
        var handler = NewHandler();
        var client = new HttpClient(handler, disposeHandler: false);
        HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/never")),
            HttpCompletionOption.ResponseHeadersRead);
        Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[64];
        Assert.True(await stream.ReadAsync(buffer) > 0);

        handler.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });
        response.Dispose();
        client.Dispose();
    }

    [Fact]
    public async Task CancellationAndDisposal_Simultaneously_NeverHang()
    {
        var handler = NewHandler();
        var client = new HttpClient(handler, disposeHandler: false);
        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> pending = client.GetAsync(
            fixture.Http("/delayed-headers?ms=30000"), cts.Token);
        await Task.Delay(200);

        Task cancelTask = Task.Run(cts.Cancel);
        Task disposeTask = Task.Run(handler.Dispose);
        await Task.WhenAll(cancelTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        client.Dispose();
    }

    [Fact]
    public async Task ManyHandlerLifecycles_RemainHealthy()
    {
        // Fast smoke of repeated create/use/dispose (deep loop lives in the
        // gated stress suite).
        for (int i = 0; i < 10; i++)
        {
            using var handler = NewHandler();
            using var client = new HttpClient(handler, disposeHandler: false);
            using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ResponseDisposedBeforeStream_AndStreamBeforeResponse_BothSafe()
    {
        // response first
        HttpResponseMessage r1 = await fixture.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json")),
            HttpCompletionOption.ResponseHeadersRead);
        Stream s1 = await r1.Content.ReadAsStreamAsync();
        r1.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            int read = await s1.ReadAsync(new byte[8]);
            throw new InvalidOperationException($"read unexpectedly succeeded ({read} bytes)");
        });

        // stream first
        HttpResponseMessage r2 = await fixture.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json")),
            HttpCompletionOption.ResponseHeadersRead);
        Stream s2 = await r2.Content.ReadAsStreamAsync();
        await s2.DisposeAsync();
        r2.Dispose();

        // handler still healthy
        using HttpResponseMessage next = await fixture.Client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }
}
