using Xunit;

namespace CurlHttp.IntegrationTests;

/// <summary>Shared server + one long-lived handler for the whole collection
/// (matching the intended production usage of a single handler instance).</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public IntegrationTestServer Server { get; private set; } = null!;
    public CurlHttpMessageHandler Handler { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public CurlHttpClientOptions BaseOptions => new()
    {
        CertificateAuthorityBundlePath = Server.CaBundlePath,
        ConnectTimeout = TimeSpan.FromSeconds(10),
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
