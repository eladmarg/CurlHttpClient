using System.Diagnostics;
using System.Net;
using Xunit;

namespace CurlHttp.IntegrationTests;

[Collection("integration")]
public class CancellationAndTimeoutTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task Cancellation_WhileAwaitingHeaders_IsPromptAndCarriesToken()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client.GetAsync(fixture.Http("/delayed-headers?ms=30000"), cts.Token));
        stopwatch.Stop();

        Assert.True(cts.Token.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"cancellation took {stopwatch.Elapsed} — not prompt");
    }

    [Fact]
    public async Task Cancellation_DuringBodyStreaming_AbortsTheRead()
    {
        using var cts = new CancellationTokenSource();
        using HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/never")),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[1024];
        int first = await stream.ReadAsync(buffer, CancellationToken.None);
        Assert.True(first > 0);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            while (await stream.ReadAsync(buffer, cts.Token) > 0)
            {
            }
        });
    }

    [Fact]
    public async Task DisposingResponse_MidStream_AbortsTheTransferWithoutHanging()
    {
        HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, fixture.Http("/never")),
            HttpCompletionOption.ResponseHeadersRead);
        Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[64];
        int firstRead = await stream.ReadAsync(buffer);
        Assert.True(firstRead > 0);

        var stopwatch = Stopwatch.StartNew();
        response.Dispose(); // must abort the native transfer, not wait for it
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));

        // The worker slot must come back: a follow-up request succeeds.
        using HttpResponseMessage next = await Client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Cancellation_DuringUpload_Aborts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var content = new StreamContent(new NeverEndingStream());
        content.Headers.ContentLength = 1L * 1024 * 1024 * 1024;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client.PostAsync(fixture.Http("/upload"), content, cts.Token));
    }

    [Fact]
    public async Task RequestTimeout_MapsToTaskCanceledWithTimeoutInner()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            RequestTimeout = TimeSpan.FromMilliseconds(500),
        });
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAsync(fixture.Http("/delayed-headers?ms=30000")));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ConnectTimeout_MapsToConnectionErrorWithTimeoutInner()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
        });
        using var client = new HttpClient(handler);

        // RFC 5737 TEST-NET-1: guaranteed non-routable.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://192.0.2.1:81/"));
        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task HttpClientTimeout_StillGovernsWhenNoRequestTimeoutConfigured()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync(fixture.Http("/delayed-headers?ms=30000")));
    }

    [Fact]
    public async Task DnsFailure_MapsToNameResolutionError()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client.GetAsync("http://this-host-does-not-exist.invalid/"));
        Assert.Equal(HttpRequestError.NameResolutionError, ex.HttpRequestError);
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(50); // trickle
            buffer.AsSpan(offset, Math.Min(count, 128)).Fill(0x2a);
            return Math.Min(count, 128);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken);
            int size = Math.Min(buffer.Length, 128);
            buffer.Span[..size].Fill(0x2a);
            return size;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
