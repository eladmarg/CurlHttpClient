using System.Net;
using System.Text;
using Xunit;

namespace CurlHttp.IntegrationTests.StatusCodes;

/// <summary>Status-code classes, informational interim responses, and
/// body-suppression semantics.</summary>
[Collection("integration")]
public class StatusCodeTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(206)]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(415)]
    [InlineData(418)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)] // nonstandard
    public async Task StatusCode_IsSurfacedNotThrown(int code)
    {
        // 301/304 via /status have no Location header → never auto-followed.
        using HttpResponseMessage response =
            await Client.GetAsync(fixture.Http($"/status/{code}"));
        Assert.Equal(code, (int)response.StatusCode);
    }

    [Fact]
    public async Task EnsureSuccessStatusCode_ThrowsOnlyForNonSuccess()
    {
        using HttpResponseMessage ok = await Client.GetAsync(fixture.Http("/json"));
        ok.EnsureSuccessStatusCode(); // no throw

        using HttpResponseMessage bad = await Client.GetAsync(fixture.Http("/status/503"));
        HttpRequestException ex = Assert.Throws<HttpRequestException>(
            () => bad.EnsureSuccessStatusCode());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task ReasonPhrase_IsExposedForHttp11()
    {
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 418 Short And Stout\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
            closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal(418, (int)response.StatusCode);
        Assert.Equal("Short And Stout", response.ReasonPhrase);
    }

    [Fact]
    public async Task SingleInterim100_IsConsumed_FinalResponseExposed()
    {
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 200 OK\r\nX-Final: yes\r\nContent-Length: 5\r\nConnection: close\r\n\r\nfinal"),
            closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("yes", response.Headers.GetValues("X-Final").Single());
        Assert.Equal("final", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MultipleInterimResponses_100Then103_AreBothConsumed()
    {
        // Two informational blocks (100, then 103 Early Hints with headers)
        // before the real response: the parser must discard both blocks'
        // headers and expose only the final 200's.
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 100 Continue\r\n\r\n" +
            "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload\r\n\r\n" +
            "HTTP/1.1 200 OK\r\nX-Real: 1\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
            closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("X-Real").Single());
        Assert.False(response.Headers.Contains("Link"),
            "103 Early Hints headers leaked into the final response");
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoContent204_HasEmptyBody_AndCompletesImmediately()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/status/204"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NotModified304_WithContentLength_SuppressesBody()
    {
        // RFC 9110: 304 has headers describing the WOULD-BE body but no body.
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 304 Not Modified\r\nContent-Length: 1234\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n"),
            closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Head_WithContentLength_SuppressesBody()
    {
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 5678\r\nConnection: close\r\n\r\n"),
            closeAbruptly: false);
        using var request = new HttpRequestMessage(HttpMethod.Head, server.BaseUri);
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5678, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Expect100Continue_OptIn_InterimBlockTraversesCorrectly()
    {
        // Scripted server: waits for the header block, sends 100 Continue,
        // reads the body, then answers. Proves the interim block ordering
        // works when the client explicitly opts into Expect: 100-continue.
        using var server = new Expect100Server();
        using var handler = new CurlHttpMessageHandler(fixture.BaseOptions);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUri)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("body-after-continue")),
        };
        request.Headers.ExpectContinue = true;

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("got:19", await response.Content.ReadAsStringAsync());
        Assert.True(server.SawExpectHeader, "the Expect: 100-continue header never reached the server");
        Assert.True(server.BodyArrivedAfterInterim,
            "body bytes were not sent after the interim 100 response");
    }

    /// <summary>Minimal scripted Expect/Continue server on a raw socket.</summary>
    private sealed class Expect100Server : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public Uri BaseUri { get; }
        public volatile bool SawExpectHeader;
        public volatile bool BodyArrivedAfterInterim;

        public Expect100Server()
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client =
                    await _listener.AcceptTcpClientAsync(_cts.Token);
                System.Net.Sockets.NetworkStream stream = client.GetStream();

                // Read the request head only.
                var head = new StringBuilder();
                byte[] buffer = new byte[8192];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        return;
                    }
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                string headText = head.ToString();
                SawExpectHeader = headText.Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase);
                int bodyInHead = head.Length - (headText.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);

                // Interim response, then read the body.
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), _cts.Token);
                int contentLength = int.Parse(headText.Split("Content-Length:")[1].Split("\r\n")[0].Trim());
                int received = bodyInHead;
                while (received < contentLength)
                {
                    int read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    received += read;
                }
                BodyArrivedAfterInterim = received == contentLength && bodyInHead == 0;

                string body = $"got:{received}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),
                    _cts.Token);
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }
    }
}
