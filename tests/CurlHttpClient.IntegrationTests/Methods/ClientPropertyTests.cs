using System.Net;
using CurlHttp.IntegrationTests.ApiCoverage;
using Xunit;

namespace CurlHttp.IntegrationTests.Methods;

/// <summary>HttpClient configuration-surface behavior over the curl handler.</summary>
[Collection("integration")]
public class ClientPropertyTests(ServerFixture fixture)
{
    private HttpClient NewClient() => new(fixture.Handler, disposeHandler: false);

    [Fact]
    [ApiCoverage("HttpClient.BaseAddress { get; set; }")]
    public async Task BaseAddress_ResolvesRelativeRequestUris()
    {
        using HttpClient client = NewClient();
        client.BaseAddress = fixture.Server.HttpBaseUri;
        Assert.Equal(fixture.Server.HttpBaseUri, client.BaseAddress);

        using HttpResponseMessage relative = await client.GetAsync("/json");
        Assert.Equal(HttpStatusCode.OK, relative.StatusCode);
        using HttpResponseMessage relativeNoSlash = await client.GetAsync("json");
        Assert.Equal(HttpStatusCode.OK, relativeNoSlash.StatusCode);
        // Absolute URI wins over BaseAddress.
        using HttpResponseMessage absolute = await client.GetAsync(fixture.Http("/echo-method"));
        Assert.Equal("GET", await absolute.Content.ReadAsStringAsync());
    }

    [Fact]
    [ApiCoverage("HttpClient.DefaultRequestHeaders { get; }")]
    public async Task DefaultRequestHeaders_AreSentOnEveryRequest_AndPerRequestHeadersWin()
    {
        using HttpClient client = NewClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Default", "from-default");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("default-agent/1.0");

        using HttpResponseMessage plain = await client.GetAsync(fixture.Http("/echo-headers"));
        string body = await plain.Content.ReadAsStringAsync();
        Assert.Contains("from-default", body);
        Assert.Contains("default-agent/1.0", body);

        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("X-Default", "per-request");
        using HttpResponseMessage overridden = await client.SendAsync(request);
        string overriddenBody = await overridden.Content.ReadAsStringAsync();
        Assert.Contains("per-request", overriddenBody);
    }

    [Fact]
    [ApiCoverage("HttpClient.Timeout { get; set; }")]
    public async Task Timeout_Property_GovernsTheRequest()
    {
        using HttpClient client = NewClient();
        client.Timeout = TimeSpan.FromMilliseconds(400);
        Assert.Equal(TimeSpan.FromMilliseconds(400), client.Timeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync(fixture.Http("/delayed-headers?ms=30000")));
    }

    [Fact]
    [ApiCoverage("HttpClient.MaxResponseContentBufferSize { get; set; }")]
    public async Task MaxResponseContentBufferSize_IsEnforcedDuringBuffering()
    {
        using HttpClient client = NewClient();
        client.MaxResponseContentBufferSize = 1024;

        // 4 KB body against a 1 KB buffering cap: HttpClient must reject it
        // (proves the handler's streaming content participates in the
        // framework's buffering limit).
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Http("/slow-body?chunks=4&delayMs=1")));
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    [ApiCoverage("HttpClient.DefaultRequestVersion { get; set; }")]
    [ApiCoverage("HttpClient.DefaultVersionPolicy { get; set; }")]
    public async Task VersionProperties_AreAccepted_ButNegotiationIsHandlerLevel()
    {
        // DOCUMENTED DIVERGENCE: this handler negotiates the HTTP version at
        // handler level (EnableHttp2), not per request. The properties are
        // settable and harmless; the response reports what was actually
        // negotiated on the wire.
        using HttpClient client = NewClient();
        client.DefaultRequestVersion = new Version(2, 0);
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        Assert.Equal(new Version(2, 0), client.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.DefaultVersionPolicy);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(new Version(1, 1), response.Version); // h2 disabled on this handler
    }

    [Fact]
    [ApiCoverage("HttpClient.CancelPendingRequests()")]
    public async Task CancelPendingRequests_AbortsInFlightRequests()
    {
        using HttpClient client = NewClient();
        Task<HttpResponseMessage> pending =
            client.GetAsync(fixture.Http("/delayed-headers?ms=30000"));
        await Task.Delay(300);

        client.CancelPendingRequests();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // The client remains usable for new requests afterwards.
        using HttpResponseMessage next = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.Dispose()")]
    public async Task Dispose_Client_KeepsSharedHandlerAlive()
    {
        var client = new HttpClient(fixture.Handler, disposeHandler: false);
        using (HttpResponseMessage warm = await client.GetAsync(fixture.Http("/json")))
        {
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        }
        client.Dispose();
        // The rejection happens synchronously inside GetAsync's argument
        // validation, before any task is produced.
        ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(
            () => { _ = client.GetAsync(fixture.Http("/json")); });
        Assert.Contains(nameof(HttpClient), thrown.ObjectName);

        // The shared handler survived the client's disposal.
        using HttpResponseMessage after = await fixture.Client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }
}
