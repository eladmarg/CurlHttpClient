using System.Diagnostics;
using System.Net;
using CurlHttp.IntegrationTests.ApiCoverage;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.SyncSend;

/// <summary>
/// Synchronous HttpClient.Send support (all overloads route through the
/// handler's Send override, which blocks on the async pipeline).
/// </summary>
[Collection("integration")]
public class SyncSendTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    [ApiCoverage("HttpClient.Send(HttpRequestMessage)")]
    public void Send_Request_Works()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json"));
        using HttpResponseMessage response = Client.Send(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.Send(HttpRequestMessage, CancellationToken)")]
    public void Send_WithCancellationToken_Works()
    {
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json"));
        using HttpResponseMessage response = Client.Send(request, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.Send(HttpRequestMessage, HttpCompletionOption)")]
    public void Send_WithCompletionOption_Works()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/json"));
        using HttpResponseMessage response = Client.Send(request, HttpCompletionOption.ResponseContentRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.Send(HttpRequestMessage, HttpCompletionOption, CancellationToken)")]
    public void Send_WithCompletionOptionAndToken_StreamsHeadersFirst()
    {
        // Body ~4 s (4 chunks x 1 s). A wide gap between header arrival and body
        // completion keeps the streaming proof robust on a slow/contended CI
        // runner (a sync Send competes for ThreadPool threads while the parallel
        // cipher-suites collection saturates the cores).
        using var request = new HttpRequestMessage(
            HttpMethod.Get, fixture.Http("/slow-body?chunks=4&delayMs=1000"));
        var stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = Client.Send(
            request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        stopwatch.Stop();

        // Headers must arrive well before the ~4 s body completes.
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"sync headers took {stopwatch.ElapsedMilliseconds} ms — buffered, not streamed");

        // Fully synchronous read of the streaming body.
        using Stream stream = response.Content.ReadAsStream();
        using var reader = new StreamReader(stream);
        Assert.Equal(4 * 1024, reader.ReadToEnd().Length);
    }

    [Fact]
    public void Send_Upload_StreamsRequestBody()
    {
        const int size = 2 * 1024 * 1024;
        using var content = new StreamContent(new DeterministicPayload.Stream2(size, seed: 5));
        content.Headers.ContentLength = size;
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/upload"))
        {
            Content = content,
        };
        using HttpResponseMessage response = Client.Send(request);
        Assert.Contains($"\"bytes\":{size}", ReadBody(response).Replace(" ", ""));
    }

    [Fact]
    public void Send_Cancellation_ThrowsPromptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, fixture.Http("/delayed-headers?ms=30000"));

        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Client.Send(request, cts.Token));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Send_HttpClientTimeout_Applies()
    {
        using var handler = new CurlHttpMessageHandler(fixture.BaseOptions);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(400) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, fixture.Http("/delayed-headers?ms=30000"));

        Assert.ThrowsAny<OperationCanceledException>(() => client.Send(request));
    }

    [Fact]
    public void Send_AsyncOnlyContent_Succeeds_UnlikeSocketsHttpHandler()
    {
        // Positive divergence, by design: our body pump runs on the worker
        // thread via the async path, so content that only implements async
        // serialization streams fine under sync Send. SocketsHttpHandler
        // throws NotSupportedException for the same request.
        using var curlRequest = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/upload"))
        {
            Content = new AsyncOnlyContent(1024),
        };
        using HttpResponseMessage response = Client.Send(curlRequest);
        Assert.Contains("\"bytes\":1024", ReadBody(response).Replace(" ", ""));

        using var sockets = new HttpClient(new SocketsHttpHandler());
        using var socketsRequest = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/upload"))
        {
            Content = new AsyncOnlyContent(1024),
        };
        Assert.Throws<NotSupportedException>(() => sockets.Send(socketsRequest));
    }

    [Fact]
    public void Send_Http2_Succeeds_UnlikeSocketsHttpHandler()
    {
        // Positive divergence: sync HTTP/2 works here because the transfer is
        // a blocking curl_easy_perform on a worker; SocketsHttpHandler's sync
        // path rejects HTTP/2.
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            EnableHttp2 = true,
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Https("/json"));
        using HttpResponseMessage response = client.Send(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(2, 0), response.Version);
    }

    private static string ReadBody(HttpResponseMessage response)
    {
        using Stream stream = response.Content.ReadAsStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class AsyncOnlyContent(int length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context)
        {
            byte[] data = DeterministicPayload.Create(length, seed: 9);
            await Task.Yield(); // genuinely asynchronous
            await stream.WriteAsync(data);
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }
}
