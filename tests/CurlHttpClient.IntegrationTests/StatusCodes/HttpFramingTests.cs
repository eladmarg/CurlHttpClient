using System.Net;
using System.Text;
using Xunit;

namespace CurlHttp.IntegrationTests.StatusCodes;

/// <summary>Wire-framing edge cases only a raw server can produce.</summary>
[Collection("integration")]
public class HttpFramingTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    private static RawTcpServer Raw(string response, bool close = false)
        => new(Encoding.ASCII.GetBytes(response), closeAbruptly: close);

    [Fact]
    public async Task Http10Response_IsParsed_AndVersionReported()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\nhello", close: true);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal(new Version(1, 0), response.Version);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CloseDelimitedBody_NoContentLength_ReadsUntilEof()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nbody-until-close", close: true);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal("body-until-close", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ContentLengthSmallerThanSentData_ExtraBytesAreIgnored()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbodyEXTRAGARBAGE",
            close: true);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal("body", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ContentLengthLargerThanBody_FailsAsTruncation()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nContent-Length: 999\r\nConnection: close\r\n\r\nshort", close: true);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
            await response.Content.ReadAsStringAsync();
        });
    }

    [Fact]
    public async Task InvalidChunkSize_FailsWithProtocolError()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
            "ZZZ\r\nnot-hex\r\n0\r\n\r\n", close: true);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
            await response.Content.ReadAsStringAsync();
        });
    }

    [Fact]
    public async Task MissingFinalChunk_FailsAsTruncation()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
            "5\r\nhello\r\n", close: true); // no 0-chunk terminator
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage response = await Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, server.BaseUri),
                HttpCompletionOption.ResponseHeadersRead);
            await using Stream stream = await response.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[1024];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });
    }

    [Fact]
    public async Task EmptyAndOneByteChunks_AreReassembledExactly()
    {
        var chunked = new StringBuilder(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
        foreach (char c in "streaming")
        {
            chunked.Append($"1\r\n{c}\r\n");
        }
        chunked.Append("0\r\n\r\n");
        using RawTcpServer server = Raw(chunked.ToString(), close: true);

        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal("streaming", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingStatusCode_FailsAsInvalidResponse()
    {
        using RawTcpServer server = Raw("HTTP/1.1 \r\n\r\n", close: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client.GetAsync(server.BaseUri));
    }

    [Fact]
    public async Task ResponseWithoutHeaderTerminator_TimesOutOrFailsCleanly()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            RequestTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = new HttpClient(handler);
        // Head never terminates; the request must end via timeout, not hang.
        using RawTcpServer server = Raw("HTTP/1.1 200 OK\r\nX-Partial: yes\r\n");
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(server.BaseUri));
    }

    [Fact]
    public async Task ConnectionResetMidBody_SurfacesAsIOException()
    {
        using RawTcpServer server = Raw(
            "HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\npartial-data", close: true);
        using HttpResponseMessage response = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, server.BaseUri),
            HttpCompletionOption.ResponseHeadersRead);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            byte[] buffer = new byte[4096];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });
    }
}
