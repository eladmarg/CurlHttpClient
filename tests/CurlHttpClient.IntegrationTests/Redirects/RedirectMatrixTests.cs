using System.Net;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests.Redirects;

/// <summary>
/// Redirect matrix: method transformation per 3xx status, security policies
/// (Authorization stripping, https downgrade), loops, and effective URL.
/// The documented policy matches libcurl defaults, which for POST equal
/// SocketsHttpHandler: 301/302/303 rewrite POST→GET; 307/308 preserve.
/// </summary>
[Collection("integration")]
public class RedirectMatrixTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    private Uri RedirectTo(int status, string target) =>
        fixture.Http($"/redirect-custom?status={status}&to={Uri.EscapeDataString(target)}");

    [Theory]
    [InlineData(301, "GET")]
    [InlineData(302, "GET")]
    [InlineData(303, "GET")]
    [InlineData(307, "POST")]
    [InlineData(308, "POST")]
    public async Task Post_MethodTransformation_PerRedirectStatus(int status, string expectedMethod)
    {
        using var content = new StringContent("payload");
        using HttpResponseMessage response =
            await Client.PostAsync(RedirectTo(status, "/echo-method"), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMethod, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Get_IsAlwaysPreservedAcrossRedirects(int status)
    {
        using HttpResponseMessage response = await Client.GetAsync(RedirectTo(status, "/echo-method"));
        Assert.Equal("GET", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RelativeAndAbsoluteLocations_BothResolve()
    {
        using HttpResponseMessage relative = await Client.GetAsync(RedirectTo(302, "/json"));
        Assert.Equal(HttpStatusCode.OK, relative.StatusCode);

        using HttpResponseMessage absolute =
            await Client.GetAsync(RedirectTo(302, fixture.Http("/json").AbsoluteUri));
        Assert.Equal(HttpStatusCode.OK, absolute.StatusCode);
        Assert.EndsWith("/json", absolute.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RedirectLoop_FailsWithTooManyRedirects_DocumentedDivergence()
    {
        // DOCUMENTED DIVERGENCE: libcurl fails the transfer
        // (CURLE_TOO_MANY_REDIRECTS → HttpRequestException); SocketsHttpHandler
        // returns the last 3xx response instead.
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client.GetAsync(fixture.Http("/redirect-loop")));
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaxRedirects_IsHonored()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MaxAutomaticRedirections = 2,
        });
        using var client = new HttpClient(handler);

        // 2 hops allowed, 2 needed → succeeds.
        using HttpResponseMessage ok = await client.GetAsync(fixture.Http("/redirect/1"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // 3 hops needed → fails.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Http("/redirect/5")));
    }

    [Fact]
    public async Task CrossHostRedirect_StripsAuthorization_SameHostKeepsIt()
    {
        // 127.0.0.1 → localhost is a HOST change on the same server:
        // libcurl (UNRESTRICTED_AUTH off) must strip Authorization.
        var localhostEcho = new UriBuilder(fixture.Http("/echo-headers")) { Host = "localhost" }.Uri;

        using var crossHost = new HttpRequestMessage(HttpMethod.Get,
            RedirectTo(302, localhostEcho.AbsoluteUri));
        crossHost.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");
        crossHost.Headers.TryAddWithoutValidation("X-Api-Key", "custom-header-caveat");
        using HttpResponseMessage crossResponse = await Client.SendAsync(crossHost);
        using JsonDocument crossHeaders =
            JsonDocument.Parse(await crossResponse.Content.ReadAsStringAsync());
        Assert.False(crossHeaders.RootElement.TryGetProperty("Authorization", out _),
            "Authorization was forwarded across a host change — credential leak");
        // DOCUMENTED CAVEAT: only Authorization/Cookie are stripped; other
        // sensitive custom headers ARE forwarded.
        Assert.True(crossHeaders.RootElement.TryGetProperty("X-Api-Key", out _));

        using var sameHost = new HttpRequestMessage(HttpMethod.Get, RedirectTo(302, "/echo-headers"));
        sameHost.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");
        using HttpResponseMessage sameResponse = await Client.SendAsync(sameHost);
        using JsonDocument sameHeaders =
            JsonDocument.Parse(await sameResponse.Content.ReadAsStringAsync());
        Assert.Equal("Bearer secret-token",
            sameHeaders.RootElement.GetProperty("Authorization").GetString());
    }

    [Fact]
    public async Task RedirectResponseBody_IsDiscarded_FinalBodyExposed()
    {
        // The 302 hop carries its own body; only the final response's body
        // may reach the caller.
        using HttpResponseMessage response = await Client.GetAsync(RedirectTo(302, "/json"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hello", body);
        Assert.DoesNotContain("redirect", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EffectiveUrl_ReflectsTheFinalHop()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/redirect/3"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("/json", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task NonReplayableBody_On307Redirect_FailsCleanly()
    {
        // 307 must re-send the body; a non-seekable stream that was already
        // consumed cannot be replayed. Expect a clean failure, not a hang or
        // a silently empty re-POST.
        var content = new StreamContent(new Infrastructure.DeterministicPayload.Stream2(
            256 * 1024, seed: 61, seekable: false));
        content.Headers.ContentLength = 256 * 1024;

        // Same-origin 307 → libcurl rewinds when possible; non-seekable stream
        // reports CANTSEEK. curl may still read-and-discard; the observable
        // contract: either success with intact body or clean HttpRequestException.
        try
        {
            using HttpResponseMessage response =
                await Client.PostAsync(RedirectTo(307, "/inspect"), content);
            using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(256 * 1024, result.RootElement.GetProperty("bodyLength").GetInt64());
        }
        catch (HttpRequestException)
        {
            // acceptable documented outcome for a non-replayable body
        }
    }
}
