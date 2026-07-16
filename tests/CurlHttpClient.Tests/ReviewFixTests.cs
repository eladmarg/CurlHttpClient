using System.Text;
using CurlHttp;
using CurlHttp.Internal;
using CurlHttp.Native;
using Xunit;

namespace CurlHttp.Tests;

/// <summary>
/// Regression tests for defects found in the deep production-readiness review.
/// Each test name references the consolidated finding id (M-nn) it locks in.
/// </summary>
public class ReviewFixTests
{
    // ---- M10 / M23 / L: options validation at construction ----

    [Fact]
    public void Validate_RejectsNegativeMaxConnectionsPerServer() // M-L
    {
        var options = new CurlHttpClientOptions { MaxConnectionsPerServer = -1 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsMaxRedirsBelowOne_EvenWhenRedirectsDisabled() // M23
    {
        var options = new CurlHttpClientOptions { AllowAutoRedirect = false, MaxAutomaticRedirections = -5 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsOverlongRequestTimeout() // M10
    {
        // > int.MaxValue ms would truncate/wrap crossing to libcurl's 32-bit long.
        var options = new CurlHttpClientOptions { RequestTimeout = TimeSpan.FromDays(30) };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_AllowsInfiniteTimeout() // M10 (boundary)
    {
        var options = new CurlHttpClientOptions { RequestTimeout = Timeout.InfiniteTimeSpan };
        options.Validate(); // must not throw
    }

    [Fact]
    public void Validate_RejectsOversizedUploadBuffer() // M-L
    {
        var options = new CurlHttpClientOptions { UploadBufferSize = 8 * 1024 * 1024 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsTinyMaxResponseHeadersLength() // M16
    {
        var options = new CurlHttpClientOptions { MaxResponseHeadersLength = 16 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    // ---- M16: header-flood cap in the parser ----

    [Fact]
    public void HeaderParser_FailsWhenHeaderBlockExceedsCap() // M16
    {
        var parser = new ResponseHeaderParser(
            new Uri("https://example.test/"), followRedirects: true,
            proxyAuthRetryPossible: false, maxHeadersLength: 4096);

        parser.OnHeaderLine(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"),
            HeaderLineFlags.StatusLine, 200);

        string bigValue = new('x', 1000);
        Assert.Throws<HttpRequestException>(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                parser.OnHeaderLine(
                    Encoding.ASCII.GetBytes($"X-Pad-{i}: {bigValue}\r\n"),
                    HeaderLineFlags.None, 200);
            }
        });
    }

    [Fact]
    public void HeaderParser_AllowsNormalHeaderBlock() // M16 (no false positive)
    {
        var parser = new ResponseHeaderParser(
            new Uri("https://example.test/"), followRedirects: true,
            proxyAuthRetryPossible: false, maxHeadersLength: 1024 * 1024);
        bool final = parser.OnHeaderLine(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"), HeaderLineFlags.StatusLine, 200);
        parser.OnHeaderLine(Encoding.ASCII.GetBytes("Content-Type: application/json\r\n"),
            HeaderLineFlags.None, 200);
        final |= parser.OnHeaderLine(Encoding.ASCII.GetBytes("\r\n"), HeaderLineFlags.BlockEnd, 200);
        Assert.True(final);
    }

    // ---- M14: single-consumer contract on the response queue ----

    [Fact]
    public async Task Queue_SecondConcurrentRead_ThrowsInsteadOfHanging() // M14
    {
        using var queue = new BoundedByteQueue(1024);
        // First read parks (no data yet).
        ValueTask<int> first = queue.ReadAsync(new byte[8], CancellationToken.None);
        Assert.False(first.IsCompleted);

        // Second overlapped read must fail loudly rather than orphan the first.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await queue.ReadAsync(new byte[8], CancellationToken.None));

        // The first read is still serviceable.
        queue.Write([1, 2, 3]);
        Assert.Equal(3, await first);
    }

    // ---- M19: aborted upload queue surfaces the abort, not a pause ----

    [Fact]
    public void Queue_TryReadUpload_AfterAbort_Throws() // M19
    {
        using var queue = new BoundedByteQueue(1024);
        queue.Abort();
        Assert.ThrowsAny<Exception>(() => queue.TryReadUpload(new byte[8]));
    }

    [Fact]
    public void Queue_TryReadUpload_WhenEmptyAndOpen_ReportsWouldBlock() // M19 (no false abort)
    {
        using var queue = new BoundedByteQueue(1024);
        Assert.Equal(-1, queue.TryReadUpload(new byte[8]));
    }
}
