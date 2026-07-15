using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests.Headers;

/// <summary>Request/response header edge cases.</summary>
[Collection("integration")]
public class HeaderBehaviorTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task RequestHeaders_LongValues_Survive()
    {
        string longValue = new string('v', 12 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("X-Long", longValue);
        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(longValue, headers.RootElement.GetProperty("X-Long").GetString());
    }

    [Fact]
    public async Task RequestHeader_EmptyValue_IsDeliveredEmpty()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("X-Empty", "");
        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(headers.RootElement.TryGetProperty("X-Empty", out JsonElement value),
            "empty-valued header was dropped");
        Assert.Equal("", value.GetString());
    }

    [Fact]
    public async Task RequestHeader_WhitespaceAroundValue_IsTrimmedPerRfc()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("X-Spacey", "  padded  ");
        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("padded", headers.RootElement.GetProperty("X-Spacey").GetString());
    }

    [Fact]
    public async Task HostHeader_DefaultsToUriAuthority_AndCanBeOverridden()
    {
        using HttpResponseMessage plain = await Client.GetAsync(fixture.Http("/echo-headers"));
        using JsonDocument headers = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
        Assert.Equal(fixture.Http("/").Authority,
            headers.RootElement.GetProperty("Host").GetString());

        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("Host", "override.example");
        using HttpResponseMessage overridden = await Client.SendAsync(request);
        using JsonDocument overriddenHeaders =
            JsonDocument.Parse(await overridden.Content.ReadAsStringAsync());
        Assert.Equal("override.example",
            overriddenHeaders.RootElement.GetProperty("Host").GetString());
    }

    [Fact]
    public async Task ContentHeaders_LandOnContentHeaders_NotResponseHeaders()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/json"));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.NonValidated.Contains("Content-Type"),
            "Content-Type leaked onto HttpResponseMessage.Headers");
        Assert.True(response.Headers.NonValidated.Contains("X-Connection-Id"),
            "general header missing from HttpResponseMessage.Headers");
    }

    [Fact]
    public async Task ResponseHeader_CommaSeparatedAndRepeated_AreDistinguishable()
    {
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/repeated-headers"));
        // Two physically separate X-Multi lines → two values.
        Assert.Equal(["a", "b"], response.Headers.GetValues("X-Multi").ToArray());
        // Set-Cookie must NEVER be comma-joined.
        Assert.Equal(2, response.Headers.GetValues("Set-Cookie").Count());
    }

    [Fact]
    public async Task ObsFoldedResponseHeader_IsUnfoldedByCurl()
    {
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "X-Folded: first\r\n second\r\n" +
            "Content-Length: 2\r\n" +
            "Connection: close\r\n\r\nok"), closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        string folded = response.Headers.GetValues("X-Folded").Single();
        Assert.Contains("first", folded);
        Assert.Contains("second", folded);
    }

    [Fact]
    public async Task ResponseHeader_Latin1Value_IsPreserved()
    {
        using var server = new RawTcpServer([
            .. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Latin: caf"),
            0xE9,
            .. Encoding.ASCII.GetBytes("\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
        ], closeAbruptly: false);
        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal("café", response.Headers.GetValues("X-Latin").Single());
    }

    [Fact]
    public async Task LargeHeaderBlock_ManyHeaders_AllArrive()
    {
        var head = new StringBuilder("HTTP/1.1 200 OK\r\n");
        for (int i = 0; i < 150; i++)
        {
            head.Append($"X-H{i}: {new string((char)('a' + i % 26), 200)}\r\n");
        }
        head.Append("Content-Length: 2\r\nConnection: close\r\n\r\nok");
        using var server = new RawTcpServer(Encoding.ASCII.GetBytes(head.ToString()), closeAbruptly: false);

        using HttpResponseMessage response = await Client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        for (int i = 0; i < 150; i += 37)
        {
            Assert.Equal(200, response.Headers.GetValues($"X-H{i}").Single().Length);
        }
    }

    [Fact]
    public async Task AuthorizationAndCorrelationHeaders_AreDelivered()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Http("/echo-headers"));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-token-not-a-secret");
        request.Headers.TryAddWithoutValidation("traceparent",
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer test-token-not-a-secret",
            headers.RootElement.GetProperty("Authorization").GetString());
        Assert.StartsWith("00-0af7651916cd43dd",
            headers.RootElement.GetProperty("traceparent").GetString());
    }
}
