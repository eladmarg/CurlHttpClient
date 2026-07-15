using System.Net;
using Xunit;

namespace CurlHttp.IntegrationTests.Pooling;

/// <summary>Connection reuse proven by SERVER-SIDE connection identifiers
/// (the test server stamps X-Connection-Id per transport connection), not by
/// timing.</summary>
[Collection("integration")]
public class ConnectionPoolingTests(ServerFixture fixture)
{
    private static async Task<string> ConnectionIdAsync(HttpClient client, Uri uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("X-Connection-Id").Single();
    }

    [Fact]
    public async Task SequentialRequests_ReuseOneConnection()
    {
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            ids.Add(await ConnectionIdAsync(fixture.Client, fixture.Http("/json")));
        }
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task DifferentOrigins_UseDifferentConnections()
    {
        string http = await ConnectionIdAsync(fixture.Client, fixture.Http("/json"));
        string https = await ConnectionIdAsync(fixture.Client, fixture.Https("/json"));
        Assert.NotEqual(http, https);
    }

    [Fact]
    public async Task ServerConnectionClose_IsHandled_NextRequestOpensNew()
    {
        using var handler = new CurlHttpMessageHandler(fixture.BaseOptions);
        using var client = new HttpClient(handler);

        // /close-after sends Connection: close.
        using (HttpResponseMessage first = await client.GetAsync(fixture.Http("/close-after")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        // Next request must succeed on a fresh connection.
        using HttpResponseMessage second = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task FailedTlsConnection_IsNotPooled()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
        });
        using var client = new HttpClient(handler);

        // A cert-mismatch host fails; a subsequent good request must still work
        // (no poisoned connection left in the pool).
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri(fixture.Server.HttpsHostnameMismatchUri, "/json")));
        using HttpResponseMessage good = await client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task PartiallyConsumedResponse_DoesNotCorruptThePool()
    {
        using var handler = new CurlHttpMessageHandler(fixture.BaseOptions);
        using var client = new HttpClient(handler);

        // Read only the first bytes of a large response, then dispose.
        using (HttpResponseMessage partial = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/large?bytes=10485760")),
            HttpCompletionOption.ResponseHeadersRead))
        {
            await using Stream stream = await partial.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[1024];
            Assert.True(await stream.ReadAsync(buffer) > 0);
        } // dispose mid-stream

        // The next request must succeed.
        using HttpResponseMessage next = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task CookieEngine_IsScrubbedBetweenRequests_NoBleed()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            EnableCookieEngineForRedirects = true,
        });
        using var client = new HttpClient(handler);

        // First request receives a Set-Cookie...
        using (HttpResponseMessage set = await client.GetAsync(fixture.Http("/set-cookie")))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }
        // ...a second request to an echo endpoint must NOT carry it (the
        // pooled easy handle was scrubbed with CURLOPT_COOKIELIST ALL).
        using HttpResponseMessage echo = await client.GetAsync(fixture.Http("/echo-headers"));
        string headers = await echo.Content.ReadAsStringAsync();
        Assert.DoesNotContain("bleedcookie", headers);
    }

    [Fact]
    public async Task PerOriginConcurrencyLimit_IsHonored()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MaxConnectionsPerServer = 2,
            MaxConcurrentRequests = 16,
        });
        using var client = new HttpClient(handler);

        // 8 slow concurrent requests, cap 2/origin → at most 2 distinct
        // connections in flight at once. Server stamps connection ids.
        Task<string>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => ConnectionIdAsync(client, fixture.Http("/slow-body?chunks=2&delayMs=200")))
            .ToArray();
        string[] ids = await Task.WhenAll(tasks);

        // With only 2 concurrent connections reused across 8 requests, the
        // distinct-id count must not exceed a small bound.
        Assert.True(ids.Distinct().Count() <= 4,
            $"observed {ids.Distinct().Count()} connections — per-origin cap not enforced");
    }
}
