using System.Net;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.Proxy;

/// <summary>Proxy matrix against the controlled ManagedProxyServer: plain
/// forwarding, CONNECT tunneling, auth, failure modes, per-URI decisions,
/// and environment-variable isolation.</summary>
[Collection("integration")]
public class ProxyBehaviorTests(ServerFixture fixture)
{
    private CurlHttpMessageHandler NewHandler(IWebProxy? proxy, TimeSpan? requestTimeout = null)
        => new(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            Proxy = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            RequestTimeout = requestTimeout,
        });

    [Fact]
    public async Task PlainHttp_GoesThroughForwardProxy_WithAbsoluteFormRequest()
    {
        using var proxy = new ManagedProxyServer();
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());

        ManagedProxyServer.ProxyRequestRecord record = Assert.Single(proxy.Requests);
        Assert.StartsWith("GET http://", record.RequestLine); // absolute-form
        Assert.Contains("/json", record.RequestLine);
    }

    [Fact]
    public async Task Https_TunnelsThroughConnect_WithCertificateValidationIntact()
    {
        using var proxy = new ManagedProxyServer();
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, proxy.ConnectCount);

        // TLS through the tunnel still verifies certificates: a wrong-name
        // target must be rejected even via CONNECT.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri(fixture.Server.HttpsHostnameMismatchUri, "/json")));
    }

    [Fact]
    public async Task ProxyBasicAuth_CredentialsFromIWebProxy_AreUsed()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.RequireBasicAuth, "proxyuser", "proxypass");
        var webProxy = new WebProxy(proxy.BaseUri)
        {
            Credentials = new NetworkCredential("proxyuser", "proxypass"),
        };
        using CurlHttpMessageHandler handler = NewHandler(webProxy);
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(proxy.Requests, r => r.ProxyAuthorization?.StartsWith("Basic ") == true);
    }

    [Fact]
    public async Task ProxyAuth_MissingCredentials_Surfaces407ForPlainHttp()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.RequireBasicAuth, "proxyuser", "proxypass");
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        Assert.Contains("Basic", response.Headers.GetValues("Proxy-Authenticate").Single());
    }

    [Fact]
    public async Task ProxyAuth_MissingCredentials_FailsConnectTunnel()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.RequireBasicAuth, "proxyuser", "proxypass");
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);

        // A 407 on CONNECT means no tunnel: hard failure for https.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Https("/json")));
    }

    [Fact]
    public async Task ProxyRejection_FailsCleanly_WithoutLeakingCredentials()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.RejectAll);
        var webProxy = new WebProxy(proxy.BaseUri)
        {
            Credentials = new NetworkCredential("secretuser", "hunter2-do-not-log"),
        };
        using CurlHttpMessageHandler handler = NewHandler(webProxy);
        using var client = new HttpClient(handler);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Https("/json")));
        Assert.DoesNotContain("hunter2", ex.ToString());
        Assert.DoesNotContain("secretuser:", ex.ToString());
    }

    [Fact]
    public async Task UnresponsiveProxy_TimesOutViaRequestTimeout()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.NeverRespond);
        using CurlHttpMessageHandler handler = NewHandler(
            new WebProxy(proxy.BaseUri), requestTimeout: TimeSpan.FromSeconds(2));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(fixture.Https("/json")));
    }

    [Fact]
    public async Task ProxyDisconnect_FailsCleanly()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.Disconnect);
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(fixture.Http("/json")));
    }

    [Fact]
    public async Task MalformedProxyResponse_FailsAsProtocolError()
    {
        using var proxy = new ManagedProxyServer(ProxyMode.Malformed);
        using CurlHttpMessageHandler handler = NewHandler(new WebProxy(proxy.BaseUri));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(fixture.Http("/json")));
    }

    [Fact]
    public async Task BypassList_SkipsTheProxy()
    {
        using var proxy = new ManagedProxyServer();
        var webProxy = new WebProxy(proxy.BaseUri)
        {
            BypassProxyOnLocal = false,
            BypassList = [@"127\.0\.0\.1"],
        };
        using CurlHttpMessageHandler handler = NewHandler(webProxy);
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(proxy.Requests); // went direct
    }

    [Fact]
    public async Task PerDestinationProxyDecision_IsHonored()
    {
        using var proxy = new ManagedProxyServer();
        using CurlHttpMessageHandler handler = NewHandler(
            new SelectiveProxy(proxy.BaseUri, proxiedPath: "/echo-method"));
        using var client = new HttpClient(handler);

        using HttpResponseMessage direct = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Empty(proxy.Requests);

        using HttpResponseMessage proxied = await client.GetAsync(fixture.Http("/echo-method"));
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task ProxyEnvironmentVariables_AreCompletelyIgnored()
    {
        // SECURITY CONTRACT: libcurl's HTTP_PROXY/HTTPS_PROXY/NO_PROXY
        // environment lookup must never influence this handler — proxy
        // behavior comes exclusively from CurlHttpClientOptions.
        using var proxy = new ManagedProxyServer();
        Environment.SetEnvironmentVariable("HTTP_PROXY", proxy.BaseUri.AbsoluteUri);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy.BaseUri.AbsoluteUri);
        Environment.SetEnvironmentVariable("ALL_PROXY", proxy.BaseUri.AbsoluteUri);
        try
        {
            using CurlHttpMessageHandler handler = NewHandler(proxy: null);
            using var client = new HttpClient(handler);

            using HttpResponseMessage http = await client.GetAsync(fixture.Http("/json"));
            using HttpResponseMessage https = await client.GetAsync(fixture.Https("/json"));
            Assert.Equal(HttpStatusCode.OK, http.StatusCode);
            Assert.Equal(HttpStatusCode.OK, https.StatusCode);
            Assert.Empty(proxy.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", null);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("ALL_PROXY", null);
        }
    }

    private sealed class SelectiveProxy(Uri proxyUri, string proxiedPath) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => proxyUri;

        public bool IsBypassed(Uri host) => !host.PathAndQuery.StartsWith(proxiedPath);
    }
}
