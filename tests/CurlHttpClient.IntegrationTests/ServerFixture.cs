using Xunit;

namespace CurlHttp.IntegrationTests;

/// <summary>Shared server + one long-lived handler for the whole collection
/// (matching the intended production usage of a single handler instance).</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public IntegrationTestServer Server { get; private set; } = null!;
    public CurlHttpMessageHandler Handler { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Set CURLHTTP_ENGINE=multi to run the shared-fixture suite
    /// against the event-loop engine instead of the dedicated-worker default.
    /// Lets the whole behavioral suite validate both engines.</summary>
    public static CurlExecutionEngine Engine =>
        Environment.GetEnvironmentVariable("CURLHTTP_ENGINE") == "multi"
            ? CurlExecutionEngine.MultiEventLoop
            : CurlExecutionEngine.DedicatedWorkers;

    public CurlHttpClientOptions BaseOptions => new()
    {
        CertificateAuthorityBundlePath = Server.CaBundlePath,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ExecutionEngine = Engine,
    };

    public async Task InitializeAsync()
    {
        Server = await IntegrationTestServer.StartAsync();
        Handler = new CurlHttpMessageHandler(BaseOptions);
        Client = new HttpClient(Handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    public Uri Http(string pathAndQuery) => new(Server.HttpBaseUri, pathAndQuery);
    public Uri Https(string pathAndQuery) => new(Server.HttpsBaseUri, pathAndQuery);

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Handler.Dispose();
        await Server.DisposeAsync();
    }
}

[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServerFixture>;
