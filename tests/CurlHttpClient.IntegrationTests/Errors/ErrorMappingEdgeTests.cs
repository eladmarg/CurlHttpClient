using System.Diagnostics;
using System.Net;
using Xunit;

namespace CurlHttp.IntegrationTests.Errors;

/// <summary>Error-mapping rows beyond the basics already covered:
/// configuration errors, connection refusal, request-object misuse, and
/// backpressure-phase cancellation.</summary>
[Collection("integration")]
public class ErrorMappingEdgeTests(ServerFixture fixture)
{
    [Fact]
    public async Task ConnectionRefused_MapsToConnectionError()
    {
        // Reserve a port and close the listener: nothing is listening there.
        int deadPort;
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            deadPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.GetAsync($"http://127.0.0.1:{deadPort}/"));
        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task UnsupportedScheme_ThrowsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Client.GetAsync("ftp://example.test/file"));
    }

    [Fact]
    public async Task RelativeUriWithoutBaseAddress_Throws()
    {
        using var handler = new CurlHttpMessageHandler(fixture.BaseOptions);
        using var client = new HttpClient(handler); // no BaseAddress
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/relative"));
    }

    [Fact]
    public async Task InvalidTls12CipherList_FailsAsSecureConnectionError()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            Tls12CipherList = "DEFINITELY-NOT-A-CIPHER",
        });
        using var client = new HttpClient(handler);
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Https("/json")));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task InvalidTls13SuiteList_FailsAsSecureConnectionError()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MinimumTlsVersion = new Version(1, 3),
            Tls13CipherSuites = "TLS_NOT_A_REAL_SUITE",
        });
        using var client = new HttpClient(handler);
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(fixture.Https("/json")));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task ReusingAnHttpRequestMessage_IsRejectedByHttpClient()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json"));
        using HttpResponseMessage first = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Client.SendAsync(request));
    }

    [Fact]
    public async Task ExceptionDetail_CarriesNativeDiagnostics_WithoutSecrets()
    {
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.GetAsync("http://this-host-does-not-exist.invalid/?token=SECRETVALUE"));
        Assert.Equal(HttpRequestError.NameResolutionError, ex.HttpRequestError);
        Assert.True(ex.Data.Contains("CurlErrorCode"), "native curl code missing from Exception.Data");
        Assert.DoesNotContain("SECRETVALUE", ex.Message);
    }

    [Fact]
    public async Task Cancellation_WhileBlockedOnBackpressure_IsPromptAndFreesTheWorker()
    {
        // Tiny bounded buffer + a fast 20 MB producer: the native transfer
        // quickly fills the queue and blocks. Cancelling must wake the
        // blocked producer (the deadlock-critical path) and free the worker.
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MaxResponseBufferBytes = 64 * 1024,
            MaxConcurrentRequests = 2,
        });
        using var client = new HttpClient(handler);

        // NOTE (framework semantics): once SendAsync(HeadersRead) returns,
        // HttpClient disposes its linked CTS, so the ORIGINAL token no longer
        // reaches the handler. Mid-body cancellation therefore flows through
        // per-read tokens + response disposal — same contract as
        // SocketsHttpHandler.
        using var cts = new CancellationTokenSource();
        HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/large?bytes=20971520")),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
        byte[] buffer = new byte[8 * 1024];
        Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);
        await Task.Delay(500); // producer is now firmly blocked on the full queue

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel(); // while the producer is provably blocked
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            int read = await stream.ReadAsync(buffer, cts.Token);
            throw new InvalidOperationException($"read unexpectedly succeeded ({read} bytes)");
        });
        // Disposal is what releases the blocked native producer + worker.
        response.Dispose();
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"backpressure-blocked cancellation took {stopwatch.Elapsed}");

        // Both workers must be free again.
        using HttpResponseMessage a = await client.GetAsync(fixture.Http("/json"));
        using HttpResponseMessage b = await client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    public async Task RepeatedAndConcurrentCancellation_IsIdempotent()
    {
        using var cts = new CancellationTokenSource();
        Task<HttpResponseMessage> pending = fixture.Client.GetAsync(
            fixture.Http("/delayed-headers?ms=30000"), cts.Token);
        await Task.Delay(200);

        Parallel.For(0, 16, _ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using HttpResponseMessage next = await fixture.Client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }
}
