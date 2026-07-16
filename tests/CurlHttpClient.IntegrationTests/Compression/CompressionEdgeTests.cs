using System.IO.Compression;
using System.Text;
using Xunit;

namespace CurlHttp.IntegrationTests.Compression;

/// <summary>Decompression edge cases: corrupt/truncated payloads, the
/// decompression-disabled configuration, and per-algorithm availability.</summary>
[Collection("integration")]
public class CompressionEdgeTests(ServerFixture fixture)
{
    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload);
        }
        return buffer.ToArray();
    }

    private static RawTcpServer ServeWithEncoding(byte[] body, string encoding) =>
        new([
            .. Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Encoding: {encoding}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n"),
            .. body,
        ], closeAbruptly: false);

    [Fact]
    public void BuildAdvertisesExpectedAlgorithms()
    {
        CurlHttpClientDiagnostics diag = fixture.Handler.GetDiagnostics();
        // The packaged build must decode gzip/deflate (libz) and brotli.
        Assert.Contains("libz", diag.Features);
        Assert.Contains("brotli", diag.Features);
        // zstd is intentionally NOT packaged — documented.
        Assert.DoesNotContain("zstd", diag.Features);
    }

    [Fact]
    public async Task InvalidGzipData_SurfacesAsReadError_NotSilentGarbage()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("this is definitely not gzip");
        using RawTcpServer server = ServeWithEncoding(garbage, "gzip");

        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            using HttpResponseMessage response = await fixture.Client.GetAsync(server.BaseUri);
            await response.Content.ReadAsStringAsync();
        });
    }

    [Fact]
    public async Task TruncatedGzipData_IsLenientlyDecoded_DocumentedBehavior()
    {
        // DOCUMENTED BEHAVIOR: libcurl's inflate path is lenient about a
        // gzip stream that ends cleanly mid-stream (the HTTP framing itself
        // was complete) — it delivers the successfully decoded PREFIX without
        // an error. Verify no garbage is produced. Truncation at the HTTP
        // layer (Content-Length mismatch) still fails, covered elsewhere.
        byte[] original = Encoding.ASCII.GetBytes(new string('x', 8192));
        byte[] valid = Gzip(original);
        byte[] truncated = valid[..(valid.Length / 2)];
        using RawTcpServer server = ServeWithEncoding(truncated, "gzip");

        using HttpResponseMessage response = await fixture.Client.GetAsync(server.BaseUri);
        byte[] decoded = await response.Content.ReadAsByteArrayAsync();
        Assert.True(decoded.Length <= original.Length);
        Assert.All(decoded, b => Assert.Equal((byte)'x', b)); // prefix only, never garbage
    }

    [Fact]
    public async Task UnsupportedContentEncoding_FailsCleanly_DocumentedDivergence()
    {
        // DOCUMENTED DIVERGENCE: when automatic decompression is enabled,
        // libcurl REJECTS a response whose Content-Encoding it cannot decode
        // (CURLE_BAD_CONTENT_ENCODING, error 61) — SocketsHttpHandler would
        // deliver the raw bytes. With decompression disabled, raw bytes pass
        // through (see DecompressionDisabled test).
        byte[] body = Encoding.ASCII.GetBytes("pretend-zstd-bytes");
        using RawTcpServer server = ServeWithEncoding(body, "zstd");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using HttpResponseMessage response = await fixture.Client.GetAsync(server.BaseUri);
            await response.Content.ReadAsStringAsync();
        });
        Assert.Contains("content", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DecompressionDisabled_DeliversCompressedBytes_AndSendsNoAcceptEncoding()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            AutomaticDecompression = false,
        });
        using var client = new HttpClient(handler);

        // No Accept-Encoding is advertised...
        using HttpResponseMessage echo = await client.GetAsync(fixture.Http("/echo-headers"));
        string headers = await echo.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Accept-Encoding", headers);

        // ...and a forced-gzip response arrives compressed with headers intact.
        byte[] payload = Encoding.UTF8.GetBytes("{\"disabled\":true}");
        byte[] gzipped = Gzip(payload);
        using RawTcpServer server = ServeWithEncoding(gzipped, "gzip");
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(gzipped, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task LargeCompressedResponse_DecodesCompletely()
    {
        byte[] payload = Infrastructure.DeterministicPayload.Create(2 * 1024 * 1024, seed: 71);
        // Make it compressible: zero every other 1 KB block.
        for (int i = 0; i < payload.Length; i += 2048)
        {
            Array.Clear(payload, i, Math.Min(1024, payload.Length - i));
        }
        byte[] gzipped = Gzip(payload);
        using RawTcpServer server = ServeWithEncoding(gzipped, "gzip");

        using HttpResponseMessage response = await fixture.Client.GetAsync(server.BaseUri);
        byte[] decoded = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload.Length, decoded.Length);
        Assert.Equal(Infrastructure.DeterministicPayload.Sha256(payload),
            Infrastructure.DeterministicPayload.Sha256(decoded));
    }

    [Fact]
    public async Task AcceptEncoding_AdvertisesExactlyTheBuiltCodecs()
    {
        using HttpResponseMessage response = await fixture.Client.GetAsync(fixture.Http("/echo-headers"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("gzip", body);
        Assert.Contains("deflate", body);
        Assert.Contains("br", body);
    }
}
