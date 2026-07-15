using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CurlHttp;
using Xunit;

namespace CurlHttp.ConsumerTests;

/// <summary>
/// Exercises the library exactly as an application would: through the
/// standard <see cref="HttpClient"/> API, with CurlHttpMessageHandler
/// consumed from the packed NuGet package. Nothing here touches the
/// library's internals.
/// </summary>
public sealed class HttpClientConsumerTests : IDisposable
{
    private readonly MiniHttpServer _server = new();
    private readonly CurlHttpMessageHandler _handler;
    private readonly HttpClient _client;

    public HttpClientConsumerTests()
    {
        // No CA path configured: the cacert.pem that shipped INSIDE the NuGet
        // package must be found automatically — part of what this suite proves.
        _handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        _client = new HttpClient(_handler, disposeHandler: false);
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
        _server.Dispose();
    }

    private Uri Url(string path) => new(_server.BaseUri, path);

    [Fact]
    public void PackagedNativeAssets_ResolveAndProveOpenSsl()
    {
        CurlHttpClientDiagnostics diagnostics = _handler.GetDiagnostics();

        Assert.True(diagnostics.TlsBackendIsOpenSsl,
            $"expected OpenSSL, got '{diagnostics.SslVersion}'");
        // Both native assets must have come from the consumer's own output
        // (delivered by the NuGet runtimes/win-x64/native convention).
        Assert.Contains(@"runtimes\win-x64\native\curl_http_bridge.dll",
            diagnostics.NativeLibraryPath);
        Assert.Contains(@"runtimes\win-x64\native\cacert.pem", diagnostics.TrustSource);
    }

    [Fact]
    public async Task Get_ReturnsBodyStatusAndHeaders()
    {
        using HttpResponseMessage response = await _client.GetAsync(Url("/hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello consumer", await response.Content.ReadAsStringAsync());
        Assert.Equal("MiniHttpServer", response.Headers.GetValues("X-Server").Single());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Post_UploadsBody_ServerSeesEveryByte()
    {
        byte[] payload = new byte[512 * 1024];
        new Random(3).NextBytes(payload);

        using var content = new ByteArrayContent(payload);
        using HttpResponseMessage response = await _client.PostAsync(Url("/echo-length"), content);

        Assert.Equal(payload.Length.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResponseHeadersRead_StreamsChunksAsTheyArrive()
    {
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, Url("/slow")),
            HttpCompletionOption.ResponseHeadersRead);

        // Headers must arrive before the ~900 ms body finishes trickling in.
        Assert.True(stopwatch.ElapsedMilliseconds < 700,
            $"headers took {stopwatch.ElapsedMilliseconds} ms — response was buffered");

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        Assert.Equal("firstsecondthird", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Cancellation_IsHonoredPromptly_WithCallerToken()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _client.GetAsync(Url("/delay"), cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"cancellation took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Redirects_AreFollowedTransparently()
    {
        using HttpResponseMessage response = await _client.GetAsync(Url("/redirect"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello consumer", await response.Content.ReadAsStringAsync());
        Assert.EndsWith("/hello", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RequestHeaders_ReachTheServer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/probe"));
        request.Headers.TryAddWithoutValidation("X-Probe", "consumer-42");

        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal("consumer-42", response.Headers.GetValues("X-Probe-Echo").Single());
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneHandlerSafely()
    {
        string[] bodies = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using HttpResponseMessage response = await _client.GetAsync(Url("/hello"));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }));

        Assert.All(bodies, body => Assert.Equal("hello consumer", body));
    }

    [Fact]
    public async Task NotFound_SurfacesAsStatusCode_NotException()
    {
        using HttpResponseMessage response = await _client.GetAsync(Url("/no-such-route"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// The reason this library exists: modern TLS from the bundled OpenSSL,
/// validated against the packaged Mozilla CA bundle. Requires outbound
/// internet access.
/// </summary>
[Trait("Category", "RequiresInternet")]
public sealed class RealWorldTlsTests
{
    [Fact]
    public async Task ModernTlsEndpoint_NegotiatesTls13_WithBundledTrust()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            RequestTimeout = TimeSpan.FromMinutes(1),
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response =
            await client.GetAsync("https://www.howsmyssl.com/a/check");
        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TLS 1.3", doc.RootElement.GetProperty("tls_version").GetString());
        Assert.Equal("Probably Okay", doc.RootElement.GetProperty("rating").GetString());
    }
}
