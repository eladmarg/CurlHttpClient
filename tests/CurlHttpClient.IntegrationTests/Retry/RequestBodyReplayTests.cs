using System.Net;
using System.Text;
using System.Text.Json;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.Retry;

/// <summary>
/// A retry policy re-sends the SAME HttpRequestMessage instance. HttpContent
/// caches the stream it hands out from ReadAsStreamAsync, so a handler that
/// reads the body through it finds an EOF stream on attempt 2 and sends nothing
/// while still declaring the original Content-Length — libcurl then fails the
/// transfer with "client read function EOF fail, only 0/N of needed bytes read".
/// This is the shape of AddStandardResilienceHandler, the Google.Apis backoff
/// handler, and the Microsoft.Graph retry middleware, all of which sit directly
/// above the primary handler.
///
/// HttpClient.SendAsync refuses to send the same message twice, so these tests
/// drive the handler through HttpMessageInvoker — which is what the delegating
/// retry handlers actually do.
/// </summary>
[Collection("integration")]
public sealed class RequestBodyReplayTests(ServerFixture fixture)
{
    private static async Task<JsonDocument> SendOnceAsync(
        HttpMessageInvoker invoker, HttpRequestMessage request)
    {
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Sends the same request instance <paramref name="attempts"/>
    /// times and returns what the server received each time.</summary>
    private async Task<List<JsonDocument>> ResendAsync(HttpRequestMessage request, int attempts = 3)
    {
        using var invoker = new HttpMessageInvoker(
            new CurlHttpMessageHandler(fixture.BaseOptions), disposeHandler: true);

        var results = new List<JsonDocument>();
        for (int i = 0; i < attempts; i++)
        {
            results.Add(await SendOnceAsync(invoker, request));
        }
        return results;
    }

    [Fact]
    public async Task StringContent_IsResentInFullOnEveryAttempt()
    {
        const string json = """{"invoice":"INV-1042","amount":19.99}""";
        byte[] expected = Encoding.UTF8.GetBytes(json);

        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        foreach (JsonDocument attempt in await ResendAsync(request))
        {
            using (attempt)
            {
                Assert.Equal(expected.Length, attempt.RootElement.GetProperty("bodyLength").GetInt64());
                Assert.Equal(DeterministicPayload.Sha256(expected),
                    attempt.RootElement.GetProperty("bodySha256").GetString());
            }
        }
    }

    [Fact]
    public async Task ByteArrayContent_IsResentInFullOnEveryAttempt()
    {
        byte[] payload = DeterministicPayload.Create(256 * 1024 + 7, seed: 71);
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = new ByteArrayContent(payload),
        };

        foreach (JsonDocument attempt in await ResendAsync(request))
        {
            using (attempt)
            {
                Assert.Equal(payload.Length, attempt.RootElement.GetProperty("bodyLength").GetInt64());
                Assert.Equal(DeterministicPayload.Sha256(payload),
                    attempt.RootElement.GetProperty("bodySha256").GetString());
            }
        }
    }

    [Fact]
    public async Task FormUrlEncodedContent_IsResentInFullOnEveryAttempt()
    {
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope", "a&b=c"),
        ]);
        byte[] expected = await form.ReadAsByteArrayAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = form,
        };

        foreach (JsonDocument attempt in await ResendAsync(request))
        {
            using (attempt)
            {
                Assert.Equal(DeterministicPayload.Sha256(expected),
                    attempt.RootElement.GetProperty("bodySha256").GetString());
                Assert.Equal("application/x-www-form-urlencoded",
                    attempt.RootElement.GetProperty("contentType").GetString());
            }
        }
    }

    [Fact]
    public async Task MultipartFormDataContent_ResendsPartsAndKeepsTheBoundary()
    {
        byte[] filePayload = DeterministicPayload.Create(64 * 1024, seed: 73);
        using var multipart = new MultipartFormDataContent
        {
            { new StringContent("value-1"), "field1" },
        };
        multipart.Add(new ByteArrayContent(filePayload), "upload", "data.bin");

        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect-form"))
        {
            Content = multipart,
        };
        using var invoker = new HttpMessageInvoker(
            new CurlHttpMessageHandler(fixture.BaseOptions), disposeHandler: true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using JsonDocument result = await SendOnceAsync(invoker, request);
            Assert.Equal("value-1",
                result.RootElement.GetProperty("fields").GetProperty("field1").GetString());
            JsonElement file = result.RootElement.GetProperty("files")[0];
            Assert.Equal(filePayload.Length, file.GetProperty("length").GetInt64());
            // The boundary survived: the server's form parser found the part.
            Assert.Equal(DeterministicPayload.Sha256(filePayload),
                file.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task StreamContent_OverASeekableStream_IsResentInFull()
    {
        byte[] payload = DeterministicPayload.Create(128 * 1024, seed: 79);
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = new StreamContent(new MemoryStream(payload)),
        };

        foreach (JsonDocument attempt in await ResendAsync(request))
        {
            using (attempt)
            {
                Assert.Equal(payload.Length, attempt.RootElement.GetProperty("bodyLength").GetInt64());
                Assert.Equal(DeterministicPayload.Sha256(payload),
                    attempt.RootElement.GetProperty("bodySha256").GetString());
            }
        }
    }

    [Fact]
    public async Task StreamContent_OverASeekableStream_AtANonZeroStart_ResendsOnlyItsOwnBytes()
    {
        // StreamContent declares Length - Position bytes. A rewind must return
        // to the content's own origin, not to absolute zero, or the retry would
        // send the skipped prefix too.
        byte[] payload = DeterministicPayload.Create(4096, seed: 83);
        var stream = new MemoryStream(payload) { Position = 1024 };
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = new StreamContent(stream),
        };
        byte[] expected = payload[1024..];

        foreach (JsonDocument attempt in await ResendAsync(request))
        {
            using (attempt)
            {
                Assert.Equal(expected.Length, attempt.RootElement.GetProperty("bodyLength").GetInt64());
                Assert.Equal(DeterministicPayload.Sha256(expected),
                    attempt.RootElement.GetProperty("bodySha256").GetString());
            }
        }
    }

    [Fact]
    public async Task NonSeekableBody_FailsTheRetryWithAClearRewindError()
    {
        // Genuinely one-shot: correct to fail, but the caller must be told WHY
        // rather than getting a raw libcurl "read function EOF fail" (code 26).
        const int size = 32 * 1024;
        var content = new StreamContent(
            new DeterministicPayload.Stream2(size, seed: 89, seekable: false));
        content.Headers.ContentLength = size;

        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/inspect"))
        {
            Content = content,
        };
        using var invoker = new HttpMessageInvoker(
            new CurlHttpMessageHandler(fixture.BaseOptions), disposeHandler: true);

        // Attempt 1 streams fine.
        using (JsonDocument first = await SendOnceAsync(invoker, request))
        {
            Assert.Equal(size, first.RootElement.GetProperty("bodyLength").GetInt64());
        }

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(request, CancellationToken.None));
        Assert.Contains("could not be rewound", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EOF fail", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttemptThatNeverReachedTheServer_LeavesTheBodyReplayable()
    {
        // A connect-time failure consumes no body, so the request must stay
        // retryable even when the content is one-shot.
        const int size = 8 * 1024;
        var content = new StreamContent(
            new DeterministicPayload.Stream2(size, seed: 97, seekable: false));
        content.Headers.ContentLength = size;

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        // Port 1 on loopback refuses immediately: connect fails, body untouched.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://127.0.0.1:1/inspect"))
        {
            Content = content,
        };
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        // The SAME request instance, re-pointed at a reachable server — this is
        // the failover shape, and it must not be poisoned by the failed attempt.
        request.RequestUri = fixture.Http("/inspect");
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(size, result.RootElement.GetProperty("bodyLength").GetInt64());
    }
}
