using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests;

[Collection("integration")]
public class HttpBehaviorTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Get_SmallJson_Succeeds()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(1, 1), response.Version);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"hello\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Diagnostics_ProveOpenSslBackend()
    {
        CurlHttpClientDiagnostics diag = fixture.Handler.GetDiagnostics();
        Assert.True(diag.TlsBackendIsOpenSsl, $"expected OpenSSL, got {diag.SslVersion}");
        Assert.StartsWith("OpenSSL/3.", diag.SslVersion);
        Assert.True(diag.SupportsAsyncDns);
        Assert.Contains("https", diag.Protocols);
        Assert.Equal("X64", diag.ProcessArchitecture);
    }

    [Fact]
    public async Task ResponseHeadersRead_ReturnsBeforeBodyCompletes()
    {
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/slow-body?chunks=6&delayMs=250")),
            HttpCompletionOption.ResponseHeadersRead);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Body takes ~1.5 s; headers must arrive well before that.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"headers took {stopwatch.ElapsedMilliseconds} ms — body was buffered, not streamed");

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        Assert.Equal(6 * 1024, total);
    }

    [Fact]
    public async Task LargeDownload_StreamsCompletely()
    {
        const long size = 50L * 1024 * 1024;
        using HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http($"/large?bytes={size}")),
            HttpCompletionOption.ResponseHeadersRead);

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        Assert.Equal(size, total);
    }

    [Fact]
    public async Task LargeUpload_KnownLength_Streams()
    {
        const int size = 20 * 1024 * 1024;
        byte[] payload = new byte[size];
        new Random(11).NextBytes(payload);

        using var content = new StreamContent(new MemoryStream(payload));
        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/upload"), content);

        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(size, result.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(size, result.RootElement.GetProperty("declaredLength").GetInt64());
    }

    [Fact]
    public async Task Upload_UnknownLength_UsesChunkedEncoding()
    {
        const int size = 3 * 1024 * 1024;
        using var content = new UnknownLengthStreamContent(new MemoryStream(new byte[size]));
        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/upload"), content);

        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(size, result.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("declaredLength").ValueKind);
        Assert.Equal("chunked", result.RootElement.GetProperty("transferEncoding").GetString());
    }

    [Fact]
    public async Task EmptyPost_SendsContentLengthZero()
    {
        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/upload"), content: null);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, result.RootElement.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task Head_ReturnsHeadersWithoutBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, fixture.Http("/json"));
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CustomVerb_IsSentVerbatim()
    {
        using var request = new HttpRequestMessage(new HttpMethod("PURGE"), fixture.Http("/echo-method"));
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal("PURGE", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redirects_AreFollowed_AndRequestUriUpdated()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/redirect/2"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("/json", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RedirectedPost_BecomesGet_MatchingDotNetSemantics()
    {
        using var content = new StringContent("data", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/redirect-post"), content);
        Assert.Equal("GET", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RedirectsDisabled_SurfaceThe302()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            AllowAutoRedirect = false,
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Http("/redirect/1"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Gzip_IsDecoded_AndFramingHeadersStripped()
    {
        // ResponseHeadersRead so header assertions see the transport headers,
        // not values recomputed after HttpContent buffers the body.
        using HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/gzip")),
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.False(response.Content.Headers.Contains("Content-Length"),
            "Content-Length must be stripped when the body was transparently decoded");

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"compressed\":true", body);
    }

    [Fact]
    public async Task RepeatedHeaders_AreNotJoined()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/repeated-headers"));

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Equal(2, cookies.Count);
        Assert.Contains(cookies, c => c.StartsWith("first=1"));
        Assert.Contains(cookies, c => c.StartsWith("second=2"));
    }

    [Fact]
    public async Task ExpectContinue_IsSuppressedByDefault()
    {
        using var content = new StringContent(new string('x', 512 * 1024));
        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/echo-headers"), content);

        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(headers.RootElement.TryGetProperty("Expect", out _),
            "libcurl's automatic 'Expect: 100-continue' should have been suppressed");
    }

    [Fact]
    public async Task CustomRequestHeaders_AreDelivered()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("X-Custom-One", "alpha");
        request.Headers.TryAddWithoutValidation("X-Custom-One", "beta");
        request.Headers.UserAgent.ParseAdd("curl-http-test/1.0");

        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("alpha", headers.RootElement.GetProperty("X-Custom-One").GetString());
        Assert.Contains("beta", headers.RootElement.GetProperty("X-Custom-One").GetString());
        Assert.Equal("curl-http-test/1.0", headers.RootElement.GetProperty("User-Agent").GetString());
    }

    [Fact]
    public async Task Trailers_AreExposedOnTrailingHeaders()
    {
        using var server = RawTcpServer.ChunkedWithTrailers();
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal("chunked body", await response.Content.ReadAsStringAsync());
        Assert.Equal("abc123", response.TrailingHeaders.GetValues("X-Checksum").Single());
    }

    [Fact]
    public async Task ConnectionIsReused_AcrossSequentialRequests()
    {
        var connectionIds = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/json"));
            connectionIds.Add(response.Headers.GetValues("X-Connection-Id").Single());
        }
        Assert.Single(connectionIds.Distinct());
    }

    [Fact]
    public async Task ParallelRequests_AllSucceedOnOneHandler()
    {
        IEnumerable<Task<string>> tasks = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/json"));
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            });
        string[] bodies = await Task.WhenAll(tasks);
        Assert.All(bodies, body => Assert.Contains("hello", body));
    }

    [Fact]
    public async Task ErrorStatusCodes_AreSurfacedNotThrown()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/status/503"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class UnknownLengthStreamContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
            => stream.CopyToAsync(target);

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => stream;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
