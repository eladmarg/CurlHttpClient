using System.Net;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests.Headers;

/// <summary>
/// Headers that .NET stores as SEVERAL values but that are NOT comma-separated
/// on the wire. `UserAgent.ParseAdd("Product/1.0 (comment)")` stores a product
/// and a comment; RFC 9110 defines User-Agent as whitespace-separated and says
/// the field must not appear more than once. Emitting one line per value made
/// the server rejoin them with "," and corrupted the value — silently, because
/// Kestrel's StringValues.ToString() joins repeated lines the same way, so the
/// ordinary /echo-headers endpoint cannot tell the two apart. These tests use
/// /echo-headers-raw (unjoined array) and differential comparison against
/// SocketsHttpHandler, which is the executable specification.
/// </summary>
[Collection("integration")]
public sealed class MultiValueHeaderTests(ServerFixture fixture) : IDisposable
{
    private const string ProductAndComment = "EasyDoxApi/1.0 (+https://easydox.io)";

    private readonly HttpClient _sockets = new(new SocketsHttpHandler());

    public void Dispose() => _sockets.Dispose();

    private static async Task<string[]> RawLinesAsync(
        HttpClient client, Uri uri, Action<HttpRequestMessage> configure, string headerName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        configure(request);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty(headerName, out JsonElement value)
            ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
            : [];
    }

    [Fact]
    public async Task UserAgent_ProductAndComment_ArrivesAsOneSpaceSeparatedLine()
    {
        string[] lines = await RawLinesAsync(
            fixture.Client, fixture.Http("/echo-headers-raw"),
            r => r.Headers.UserAgent.ParseAdd(ProductAndComment), "User-Agent");

        // Exactly ONE header line — two lines is the defect, and would be
        // rejoined by the server as "EasyDoxApi/1.0,(+https://easydox.io)".
        Assert.Equal([ProductAndComment], lines);
    }

    [Fact]
    public async Task UserAgent_ProductAndComment_MatchesSocketsHttpHandler()
    {
        Uri uri = fixture.Http("/echo-headers-raw");
        static void Configure(HttpRequestMessage r) => r.Headers.UserAgent.ParseAdd(ProductAndComment);

        Assert.Equal(
            await RawLinesAsync(_sockets, uri, Configure, "User-Agent"),
            await RawLinesAsync(fixture.Client, uri, Configure, "User-Agent"));
    }

    [Fact]
    public async Task Cookie_MultipleCrumbs_MatchesSocketsHttpHandler()
    {
        // Cookie's separator is "; ", not ", ".
        Uri uri = fixture.Http("/echo-headers-raw");
        static void Configure(HttpRequestMessage r)
        {
            r.Headers.Add("Cookie", "a=1");
            r.Headers.Add("Cookie", "b=2");
        }

        string[] sockets = await RawLinesAsync(_sockets, uri, Configure, "Cookie");
        string[] curl = await RawLinesAsync(fixture.Client, uri, Configure, "Cookie");

        Assert.Equal(sockets, curl);
        Assert.Equal(["a=1; b=2"], curl);
    }

    [Fact]
    public async Task Accept_GenuineListHeader_StillJoinsWithCommas()
    {
        // The fix must not regress real comma-list headers.
        Uri uri = fixture.Http("/echo-headers-raw");
        static void Configure(HttpRequestMessage r)
        {
            r.Headers.Accept.ParseAdd("text/plain");
            r.Headers.Accept.ParseAdd("application/json");
        }

        string[] sockets = await RawLinesAsync(_sockets, uri, Configure, "Accept");
        string[] curl = await RawLinesAsync(fixture.Client, uri, Configure, "Accept");

        Assert.Equal(sockets, curl);
        Assert.Equal(["text/plain, application/json"], curl);
    }

    [Fact]
    public async Task CustomMultiValueHeader_IsSentAsASingleLine()
    {
        string[] lines = await RawLinesAsync(
            fixture.Client, fixture.Http("/echo-headers-raw"),
            r =>
            {
                r.Headers.TryAddWithoutValidation("X-Custom-One", "alpha");
                r.Headers.TryAddWithoutValidation("X-Custom-One", "beta");
            },
            "X-Custom-One");

        Assert.Equal(["alpha, beta"], lines);
    }

    [Fact]
    public async Task MultiValuedContentHeader_IsSentAsASingleLine()
    {
        // Content headers go through the same builder loop.
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentLanguage.Add("en");
        content.Headers.ContentLanguage.Add("fr");

        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Http("/echo-headers-raw"))
        {
            Content = content,
        };
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] lines =
            [.. doc.RootElement.GetProperty("Content-Language").EnumerateArray()
                .Select(v => v.GetString() ?? string.Empty)];
        Assert.Equal(["en, fr"], lines);
    }
}
