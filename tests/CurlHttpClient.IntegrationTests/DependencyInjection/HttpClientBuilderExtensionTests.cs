using System.Net;
using CurlHttp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace CurlHttp.IntegrationTests.DependencyInjection;

/// <summary>
/// The IHttpClientFactory wiring. These tests prove the curl transport is
/// genuinely in the pipeline rather than merely registered: the integration
/// server presents a certificate from a CA this test suite mints itself, which
/// is NOT in the Windows trust store, so a request only succeeds when a
/// CurlHttpMessageHandler configured with that CA bundle is actually carrying
/// it. A default SocketsHttpHandler would fail the handshake.
/// </summary>
[Collection("integration")]
public class HttpClientBuilderExtensionTests(ServerFixture fixture)
{
    private CurlHttpClientOptions Options() => new()
    {
        CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
    };

    [Fact]
    public async Task UseCurlHandler_AttachesTheTransportToAnExistingNamedClient()
    {
        var services = new ServiceCollection();
        // A client registered WITHOUT any knowledge of this package — the shape
        // an adopter already has (typed clients, Refit, vendor SDKs).
        services.AddHttpClient("existing").UseCurlHandler(_ => Options());

        using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("existing");

        using HttpResponseMessage response = await client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UseCurlHandler_WorksOnATypedClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<JsonProbe>().UseCurlHandler(Options());

        using ServiceProvider provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<JsonProbe>();

        Assert.Contains("hello", await probe.GetAsync(fixture.Https("/json")));
    }

    [Fact]
    public void UseCurlHandler_SetsAnInfiniteHandlerLifetime()
    {
        // The factory's default 2-minute rotation would discard native
        // connection pools for no benefit; forgetting this is the easiest
        // mistake when wiring the handler up by hand.
        var services = new ServiceCollection();
        services.AddHttpClient("lifetime").UseCurlHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get("lifetime");

        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
    }

    [Fact]
    public async Task AddCurlHttpClient_StillRegistersANewNamedClient()
    {
        var services = new ServiceCollection();
        services.AddCurlHttpClient("modern-tls", _ => Options());

        using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("modern-tls");

        using HttpResponseMessage response = await client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class JsonProbe(HttpClient client)
    {
        public Task<string> GetAsync(Uri uri) => client.GetStringAsync(uri);
    }
}
