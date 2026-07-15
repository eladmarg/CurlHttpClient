using System.Text;
using CurlHttp.Internal;
using CurlHttp.Native;
using Xunit;

namespace CurlHttp.Tests;

public class ResponseHeaderParserTests
{
    private static readonly Uri BaseUri = new("https://example.test/api");

    private static bool Feed(ResponseHeaderParser parser, string line, HeaderLineFlags flags, int status)
        => parser.OnHeaderLine(Encoding.ASCII.GetBytes(line), flags, status);

    private static bool FeedBlock(ResponseHeaderParser parser, int status, string statusLine,
        params string[] headers)
    {
        bool final = Feed(parser, statusLine + "\r\n", HeaderLineFlags.StatusLine, status);
        foreach (string header in headers)
        {
            final |= Feed(parser, header + "\r\n", HeaderLineFlags.None, status);
        }
        final |= Feed(parser, "\r\n", HeaderLineFlags.BlockEnd, status);
        return final;
    }

    [Fact]
    public void SimpleOkResponse_IsFinalAtBlockEnd()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = FeedBlock(parser, 200, "HTTP/1.1 200 OK",
            "Content-Type: application/json", "X-Custom: v");

        Assert.True(final);
        Assert.Equal(200, parser.StatusCode);
        Assert.Equal("OK", parser.ReasonPhrase);
        Assert.Equal(new Version(1, 1), parser.HttpVersion2);
        Assert.Contains(parser.Headers, h => h.Name == "Content-Type" && h.Value == "application/json");
    }

    [Fact]
    public void InformationalBlock_IsDiscarded()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = Feed(parser, "HTTP/1.1 100 Continue\r\n",
            HeaderLineFlags.StatusLine | HeaderLineFlags.Informational, 100);
        final |= Feed(parser, "\r\n", HeaderLineFlags.BlockEnd | HeaderLineFlags.Informational, 100);
        Assert.False(final);

        final = FeedBlock(parser, 200, "HTTP/1.1 200 OK", "Content-Length: 2");
        Assert.True(final);
        Assert.Equal(200, parser.StatusCode);
    }

    [Fact]
    public void RedirectBlockWithLocation_WhileFollowing_IsNotImmediatelyFinal()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = FeedBlock(parser, 302, "HTTP/1.1 302 Found",
            "Location: https://example.test/next");
        Assert.False(final);

        // Next block supersedes it (curl followed the redirect).
        final = FeedBlock(parser, 200, "HTTP/1.1 200 OK", "Content-Type: text/plain");
        Assert.True(final);
        Assert.Equal(200, parser.StatusCode);
        Assert.Equal(new Uri("https://example.test/next"), parser.CurrentUri);
    }

    [Fact]
    public void RedirectBlock_PromotedByTransferCompletion_WhenCurlDidNotFollow()
    {
        // e.g. redirect budget exhausted: the parked 3xx IS the final response.
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = FeedBlock(parser, 302, "HTTP/1.1 302 Found",
            "Location: https://example.test/next");
        Assert.False(final);
        Assert.True(parser.OnTransferCompleted());
        Assert.Equal(302, parser.StatusCode);
    }

    [Fact]
    public void RedirectBlock_PromotedByFirstBodyByte()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        FeedBlock(parser, 302, "HTTP/1.1 302 Found", "Location: /next");
        Assert.True(parser.OnBodyStarted());
        Assert.Equal(302, parser.StatusCode);
        // Relative Location resolved against the request URI.
        Assert.Equal(new Uri("https://example.test/next"), parser.CurrentUri);
    }

    [Fact]
    public void RedirectBlock_WithRedirectsDisabled_IsImmediatelyFinal()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: false);
        bool final = FeedBlock(parser, 302, "HTTP/1.1 302 Found", "Location: /next");
        Assert.True(final);
        Assert.Equal(302, parser.StatusCode);
        Assert.Equal(BaseUri, parser.CurrentUri);
    }

    [Fact]
    public void RedirectBlock_WithoutLocation_IsImmediatelyFinal()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = FeedBlock(parser, 304, "HTTP/1.1 304 Not Modified", "ETag: \"x\"");
        Assert.True(final);
        Assert.Equal(304, parser.StatusCode);
    }

    [Fact]
    public void Http2StatusLine_ParsesVersionAndEmptyReason()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = FeedBlock(parser, 200, "HTTP/2 200", "content-type: text/plain");
        Assert.True(final);
        Assert.Equal(new Version(2, 0), parser.HttpVersion2);
        Assert.Equal(string.Empty, parser.ReasonPhrase);
    }

    [Fact]
    public void Trailers_AreCollectedSeparately()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        FeedBlock(parser, 200, "HTTP/1.1 200 OK", "Transfer-Encoding: chunked");
        Feed(parser, "X-Checksum: abc\r\n", HeaderLineFlags.Trailer, 200);
        Feed(parser, "\r\n", HeaderLineFlags.Trailer | HeaderLineFlags.BlockEnd, 200);

        Assert.Single(parser.Trailers);
        Assert.Equal(("X-Checksum", "abc"), parser.Trailers[0]);
        Assert.DoesNotContain(parser.Headers, h => h.Name == "X-Checksum");
    }

    [Fact]
    public void RepeatedHeaders_ArePreservedAsSeparateEntries()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        FeedBlock(parser, 200, "HTTP/1.1 200 OK",
            "Set-Cookie: a=1; Path=/", "Set-Cookie: b=2; Path=/");
        Assert.Equal(2, parser.Headers.Count(h => h.Name == "Set-Cookie"));
    }

    [Fact]
    public void Latin1HeaderValues_SurviveDecoding()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        bool final = Feed(parser, "HTTP/1.1 200 OK\r\n", HeaderLineFlags.StatusLine, 200);
        byte[] line = [.. Encoding.ASCII.GetBytes("X-Name: caf"), 0xE9, (byte)'\r', (byte)'\n'];
        parser.OnHeaderLine(line, HeaderLineFlags.None, 200);
        final |= Feed(parser, "\r\n", HeaderLineFlags.BlockEnd, 200);

        Assert.True(final);
        Assert.Contains(parser.Headers, h => h.Name == "X-Name" && h.Value == "café");
    }

    [Fact]
    public void MultipleRedirectHops_TrackFinalUri()
    {
        var parser = new ResponseHeaderParser(BaseUri, followRedirects: true);
        FeedBlock(parser, 301, "HTTP/1.1 301 Moved", "Location: https://a.test/1");
        FeedBlock(parser, 302, "HTTP/1.1 302 Found", "Location: /2");
        bool final = FeedBlock(parser, 200, "HTTP/1.1 200 OK");
        Assert.True(final);
        Assert.Equal(new Uri("https://a.test/2"), parser.CurrentUri);
    }
}
