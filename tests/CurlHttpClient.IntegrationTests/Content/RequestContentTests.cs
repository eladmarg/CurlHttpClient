using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CurlHttp.IntegrationTests.ApiCoverage;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.Content;

/// <summary>
/// Request-content matrix: every HttpContent kind, verified byte-for-byte
/// against what the server ACTUALLY received (method, framing headers,
/// length, SHA-256) via the /inspect endpoint.
/// </summary>
[Collection("integration")]
public class RequestContentTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;
    private Uri Inspect => fixture.Http("/inspect");

    private async Task<JsonDocument> PostAndInspectAsync(HttpContent? content,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, Inspect)
        {
            Content = content,
        };
        using HttpResponseMessage response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoContent_SendsContentLengthZeroForPost()
    {
        using JsonDocument result = await PostAndInspectAsync(null);
        Assert.Equal(0, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(0, result.RootElement.GetProperty("declaredContentLength").GetInt64());
    }

    [Fact]
    public async Task EmptyStringContent_IsDistinctFromNoContent()
    {
        using JsonDocument result = await PostAndInspectAsync(new StringContent(""));
        Assert.Equal(0, result.RootElement.GetProperty("bodyLength").GetInt64());
        // Content-Type header proves an (empty) entity was declared.
        Assert.StartsWith("text/plain",
            result.RootElement.GetProperty("contentType").GetString());
    }

    [Fact]
    [ApiCoverage("HttpContent:StringContent")]
    public async Task StringContent_Utf8AndNonAscii_ArrivesByteExact()
    {
        const string text = "héllo wörld — עברית 你好 🚀";
        byte[] expected = Encoding.UTF8.GetBytes(text);
        using JsonDocument result = await PostAndInspectAsync(
            new StringContent(text, Encoding.UTF8, "text/plain"));

        Assert.Equal(expected.Length, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(DeterministicPayload.Sha256(expected),
            result.RootElement.GetProperty("bodySha256").GetString());
        Assert.Contains("charset=utf-8", result.RootElement.GetProperty("contentType").GetString());
    }

    [Fact]
    [ApiCoverage("HttpContent:ByteArrayContent")]
    public async Task ByteArrayContent_ArrivesByteExact()
    {
        byte[] payload = DeterministicPayload.Create(64 * 1024 + 17, seed: 21);
        using JsonDocument result = await PostAndInspectAsync(new ByteArrayContent(payload));
        Assert.Equal(payload.Length, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(DeterministicPayload.Sha256(payload),
            result.RootElement.GetProperty("bodySha256").GetString());
        Assert.Equal(payload.Length, result.RootElement.GetProperty("declaredContentLength").GetInt64());
    }

    [Fact]
    [ApiCoverage("HttpContent:ReadOnlyMemoryContent")]
    public async Task ReadOnlyMemoryContent_ArrivesByteExact()
    {
        byte[] payload = DeterministicPayload.Create(64 * 1024 + 9, seed: 23);
        using JsonDocument result = await PostAndInspectAsync(
            new ReadOnlyMemoryContent(payload.AsMemory()));
        Assert.Equal(payload.Length, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(DeterministicPayload.Sha256(payload),
            result.RootElement.GetProperty("bodySha256").GetString());
        Assert.Equal(payload.Length,
            result.RootElement.GetProperty("declaredContentLength").GetInt64());
    }

    [Fact]
    [ApiCoverage("HttpContent:JsonContent")]
    public async Task JsonContent_Created_SerializesWithJsonContentType()
    {
        // Compute the expected bytes through JsonContent itself (a fresh instance)
        // so the assertion can't drift from JsonContent's own serializer options —
        // the same round-trip pattern the FormUrlEncodedContent test uses.
        var payload = new { hello = "world", n = 42 };
        byte[] expected = await JsonContent.Create(payload).ReadAsByteArrayAsync();

        using JsonDocument result = await PostAndInspectAsync(JsonContent.Create(payload));
        Assert.StartsWith("application/json",
            result.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(DeterministicPayload.Sha256(expected),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    [ApiCoverage("HttpContent:FormUrlEncodedContent")]
    public async Task FormUrlEncodedContent_ArrivesWithCorrectTypeAndEncoding()
    {
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("alpha", "one two"),
            new KeyValuePair<string, string>("beta", "a&b=c"),
        ]);
        byte[] expected = await form.ReadAsByteArrayAsync();
        using JsonDocument result = await PostAndInspectAsync(form);
        Assert.Equal("application/x-www-form-urlencoded",
            result.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(DeterministicPayload.Sha256(expected),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    [ApiCoverage("HttpContent:MultipartFormDataContent")]
    public async Task MultipartFormDataContent_PartsAndBoundariesSurviveIntact()
    {
        byte[] filePayload = DeterministicPayload.Create(128 * 1024, seed: 31);
        using var multipart = new MultipartFormDataContent
        {
            { new StringContent("value-1"), "field1" },
            { new StringContent("value-2"), "field2" },
        };
        var file = new ByteArrayContent(filePayload);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(file, "upload", "data.bin");

        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/inspect-form"), multipart);
        response.EnsureSuccessStatusCode();
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("value-1", result.RootElement.GetProperty("fields").GetProperty("field1").GetString());
        Assert.Equal("value-2", result.RootElement.GetProperty("fields").GetProperty("field2").GetString());
        JsonElement fileElement = result.RootElement.GetProperty("files")[0];
        Assert.Equal("upload", fileElement.GetProperty("name").GetString());
        Assert.Equal("data.bin", fileElement.GetProperty("fileName").GetString());
        Assert.Equal(filePayload.Length, fileElement.GetProperty("length").GetInt64());
        Assert.Equal(DeterministicPayload.Sha256(filePayload), fileElement.GetProperty("sha256").GetString());
    }

    [Fact]
    [ApiCoverage("HttpContent:StreamContent")]
    public async Task StreamContent_KnownLength_UsesContentLengthFraming()
    {
        const int size = 512 * 1024;
        var content = new StreamContent(new DeterministicPayload.Stream2(size, seed: 41));
        content.Headers.ContentLength = size;

        using JsonDocument result = await PostAndInspectAsync(content);
        Assert.Equal(size, result.RootElement.GetProperty("declaredContentLength").GetInt64());
        Assert.Equal("", result.RootElement.GetProperty("transferEncoding").GetString());
        Assert.Equal(DeterministicPayload.ExpectedSha256(size, 41),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task StreamContent_UnknownLength_UsesChunkedFraming()
    {
        const int size = 300 * 1024;
        var content = new StreamContent(
            new DeterministicPayload.Stream2(size, seed: 43, seekable: false));
        // No ContentLength header: the handler must switch to chunked.
        using JsonDocument result = await PostAndInspectAsync(content);
        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("declaredContentLength").ValueKind);
        Assert.Equal("chunked", result.RootElement.GetProperty("transferEncoding").GetString());
        Assert.Equal(DeterministicPayload.ExpectedSha256(size, 43),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task NonSeekableTinyChunkProducer_ArrivesByteExact()
    {
        const int size = 100 * 1024;
        var content = new StreamContent(new DeterministicPayload.Stream2(
            size, seed: 47, seekable: false, maxChunk: 7)); // pathological producer
        using JsonDocument result = await PostAndInspectAsync(content);
        Assert.Equal(size, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(DeterministicPayload.ExpectedSha256(size, 47),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task CustomHttpContent_SerializeToStreamAsync_IsUsed()
    {
        using JsonDocument result = await PostAndInspectAsync(new RepeatingContent((byte)'z', 4096));
        Assert.Equal(4096, result.RootElement.GetProperty("bodyLength").GetInt64());
        Assert.Equal(DeterministicPayload.Sha256(
                Enumerable.Repeat((byte)'z', 4096).ToArray()),
            result.RootElement.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task Upload_IsStreamedIncrementally_NotBufferedBeforeSend()
    {
        // A slow producer trickles ~2.4 s of body. If the handler buffered the
        // entire upload before transmitting, the server's first byte would
        // arrive at ≈ totalMs; streaming delivers it almost immediately.
        const int chunks = 48;
        const int chunkSize = 16 * 1024;
        var content = new StreamContent(new DeterministicPayload.Stream2(
            chunks * chunkSize, seed: 53, seekable: false,
            maxChunk: chunkSize, delayPerRead: TimeSpan.FromMilliseconds(50)));

        using HttpResponseMessage response =
            await Client.PostAsync(fixture.Http("/upload-probe"), content);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        long firstByteMs = result.RootElement.GetProperty("firstByteMs").GetInt64();
        long totalMs = result.RootElement.GetProperty("totalMs").GetInt64();
        Assert.Equal(chunks * chunkSize, result.RootElement.GetProperty("bytes").GetInt64());
        Assert.True(totalMs >= 1500, $"producer finished suspiciously fast ({totalMs} ms)");
        Assert.True(firstByteMs < totalMs / 3,
            $"first body byte arrived at {firstByteMs} ms of {totalMs} ms — upload was buffered, not streamed");
    }

    [Fact]
    public async Task StreamThatThrowsMidway_FailsTheRequestWithTheOriginalError()
    {
        var content = new StreamContent(new ThrowingStream(failAfterBytes: 64 * 1024));
        content.Headers.ContentLength = 10 * 1024 * 1024;

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client.PostAsync(Inspect, content));
        Assert.Contains("request content stream", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<InvalidDataException>(ex.InnerException);
    }

    [Fact]
    public async Task ContentDisposedBeforeSend_FailsCleanly()
    {
        var content = new StringContent("payload");
        content.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => Client.PostAsync(Inspect, content));
        // The handler must remain healthy afterwards.
        using HttpResponseMessage next = await Client.GetAsync(fixture.Http("/json"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    private sealed class RepeatingContent(byte value, int length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            byte[] block = new byte[Math.Min(length, 1024)];
            Array.Fill(block, value);
            int remaining = length;
            while (remaining > 0)
            {
                int size = Math.Min(block.Length, remaining);
                await stream.WriteAsync(block.AsMemory(0, size));
                remaining -= size;
            }
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }
    }

    private sealed class ThrowingStream(long failAfterBytes) : Stream
    {
        private long _produced;

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
            if (_produced >= failAfterBytes)
            {
                throw new InvalidDataException("simulated producer failure");
            }
            int size = (int)Math.Min(count, failAfterBytes - _produced);
            buffer.AsSpan(offset, size).Clear();
            _produced += size;
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
