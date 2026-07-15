using System.Net;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests.Methods;

/// <summary>Standard-but-uncommon and fully custom HTTP verbs.</summary>
[Collection("integration")]
public class CustomVerbTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("PROPFIND")]
    [InlineData("REPORT")]
    [InlineData("MKCOL")]
    [InlineData("LOCK")]
    [InlineData("UNLOCK")]
    [InlineData("FROBNICATE")] // fully custom
    public async Task CustomVerb_ReachesTheServerVerbatim(string verb)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), fixture.Http("/echo-method"));
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal(verb, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task VerbCase_IsPreservedExactly()
    {
        // Method names are case-sensitive tokens; the handler must not
        // normalize them.
        using var request = new HttpRequestMessage(new HttpMethod("PaTcHy"), fixture.Http("/echo-method"));
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal("PaTcHy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("PROPFIND")]
    [InlineData("REPORT")]
    public async Task CustomVerb_WithRequestBody_DeliversTheBody(string verb)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), fixture.Http("/upload"))
        {
            Content = new ByteArrayContent(new byte[1234]),
        };
        using HttpResponseMessage response = await Client.SendAsync(request);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1234, result.RootElement.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task Trace_AgainstControlledRawServer()
    {
        // TRACE echoes the request; only ever run against our own raw server.
        using var server = new RawTcpServer(System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: message/http\r\nContent-Length: 4\r\nConnection: close\r\n\r\necho"));
        using var request = new HttpRequestMessage(HttpMethod.Trace, server.BaseUri);
        using HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("message/http", response.Content.Headers.ContentType?.MediaType);
    }
}
