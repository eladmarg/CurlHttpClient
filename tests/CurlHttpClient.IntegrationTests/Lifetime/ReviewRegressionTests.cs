using Xunit;

namespace CurlHttp.IntegrationTests.Lifetime;

/// <summary>
/// End-to-end regressions for the deep-review findings whose repro needs a live
/// transfer: the multi-engine failed-request cleanup race (M1), dispose-under-
/// load never hanging a send (M2/M3/M5), and origin-gate saturation on dispose
/// (M5). Each constructs its own handler with an explicit engine so it exercises
/// the fix regardless of the CURLHTTP_ENGINE the rest of the suite runs under.
/// </summary>
[Collection("integration")]
public class ReviewRegressionTests(ServerFixture fixture)
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    private CurlHttpMessageHandler NewHandler(
        CurlExecutionEngine engine, int maxConcurrent = 16, int maxPerServer = 0,
        TimeSpan? connectTimeout = null) =>
        new(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
            ExecutionEngine = engine,
            MaxConcurrentRequests = maxConcurrent,
            MaxConnectionsPerServer = maxPerServer,
        });

    /// <summary>A fixed loopback endpoint with nothing listening — connect() is
    /// refused immediately and deterministically. Intentionally NOT a bound-then-
    /// freed ephemeral port: under CI's parallel test load a freed port can be
    /// re-bound by another socket, turning an expected "refused" into a real
    /// connection and breaking the test. Port 1 (TCPMUX) is never a listener.</summary>
    private static readonly Uri RefusedEndpoint = new("http://127.0.0.1:1/");

    [Theory]
    [InlineData(CurlExecutionEngine.MultiEventLoop)] // M1 is multi-specific
    [InlineData(CurlExecutionEngine.DedicatedWorkers)]
    public async Task ManyFailedRequests_DoNotCorruptState_AndSlotsRecover(CurlExecutionEngine engine) // M1
    {
        // Short connect timeout so the test is bounded even if the loopback
        // stack drops the SYN instead of sending an immediate RST.
        using var handler = NewHandler(engine, maxConcurrent: 16,
            connectTimeout: TimeSpan.FromSeconds(2));
        using var client = new HttpClient(handler, disposeHandler: false);
        Uri refused = RefusedEndpoint;

        // A burst of pre-header failures is exactly the path where the multi
        // engine used to double-free the GCHandle / double-return the buffer
        // and leak admission slots. Nothing may crash; every send must fault.
        async Task Fire()
        {
            for (int i = 0; i < 8; i++)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(refused));
            }
        }
        Task[] workers = [.. Enumerable.Range(0, 6).Select(_ => Fire())];
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        // If slots had leaked, this would time out at admission.
        using var ok = await client.GetAsync(fixture.Http("/json")).WaitAsync(Watchdog);
        Assert.True(ok.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData(CurlExecutionEngine.MultiEventLoop)]
    [InlineData(CurlExecutionEngine.DedicatedWorkers)]
    public async Task DisposeUnderLoad_CompletesEveryPendingSend(CurlExecutionEngine engine) // M2 / M3 / M5
    {
        var handler = NewHandler(engine, maxConcurrent: 4);
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        // More requests than workers/slots: some run, some are queued or parked
        // on admission. Under the old worker bug the queued ones hung forever
        // after dispose; under the origin-gate bug a parked one hung.
        var pending = new List<Task>();
        for (int i = 0; i < 24; i++)
        {
            pending.Add(client.GetAsync(fixture.Http("/delayed-headers?ms=8000")));
        }
        await Task.Delay(200);

        handler.Dispose();

        // Every send must settle (success or exception) — none may hang.
        foreach (Task t in pending)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => t).WaitAsync(Watchdog);
        }
        client.Dispose();
    }

    [Theory]
    [InlineData(CurlExecutionEngine.MultiEventLoop)]
    [InlineData(CurlExecutionEngine.DedicatedWorkers)]
    public async Task DisposeWithOriginGateSaturation_DoesNotHang(CurlExecutionEngine engine) // M5
    {
        var handler = NewHandler(engine, maxConcurrent: 16, maxPerServer: 1);
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        // First request takes the single per-origin permit and holds it.
        Task first = client.GetAsync(fixture.Http("/delayed-headers?ms=8000"));
        await Task.Delay(200);
        // Second parks on the origin gate with a non-cancellable token — the
        // exact shape that used to hang forever on dispose (no gate timeout).
        Task second = client.GetAsync(fixture.Http("/delayed-headers?ms=8000"), CancellationToken.None);
        await Task.Delay(200);

        handler.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => first).WaitAsync(Watchdog);
        await Assert.ThrowsAnyAsync<Exception>(() => second).WaitAsync(Watchdog);
        client.Dispose();
    }
}
