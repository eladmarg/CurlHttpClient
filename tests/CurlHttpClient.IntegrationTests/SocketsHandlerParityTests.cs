using System.Net;
using System.Text.Json;
using Xunit;

namespace CurlHttp.IntegrationTests;

/// <summary>
/// Runs the same scenario through CurlHttpMessageHandler and
/// SocketsHttpHandler and asserts equivalent observable behaviour —
/// SocketsHttpHandler is the executable specification for .NET semantics.
/// </summary>
[Collection("integration")]
public sealed class SocketsHandlerParityTests(ServerFixture fixture) : IDisposable
{
    private readonly HttpClient _sockets = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    });

    public void Dispose() => _sockets.Dispose();

    private async Task<(HttpResponseMessage Curl, HttpResponseMessage Sockets)> BothAsync(
        Func<HttpClient, Task<HttpResponseMessage>> send)
        => (await send(fixture.Client), await send(_sockets));

    [Fact]
    public async Task DecompressedResponses_StripTheSameHeaders()
    {
        (HttpResponseMessage curl, HttpResponseMessage sockets) = await BothAsync(
            c => c.GetAsync(fixture.Http("/gzip"), HttpCompletionOption.ResponseHeadersRead));
        using (curl)
        using (sockets)
        {
            Assert.Equal(sockets.Content.Headers.ContentEncoding, curl.Content.Headers.ContentEncoding);
            Assert.Equal(sockets.Content.Headers.Contains("Content-Length"),
                curl.Content.Headers.Contains("Content-Length"));
            Assert.Equal(await sockets.Content.ReadAsStringAsync(),
                await curl.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task RedirectedPost_RewritesMethodTheSameWay()
    {
        (HttpResponseMessage curl, HttpResponseMessage sockets) = await BothAsync(
            c => c.PostAsync(fixture.Http("/redirect-post"), new StringContent("x")));
        using (curl)
        using (sockets)
        {
            Assert.Equal(await sockets.Content.ReadAsStringAsync(),
                await curl.Content.ReadAsStringAsync()); // both "GET"
        }
    }

    [Fact]
    public async Task RedirectChains_EndOnTheSameUriAndStatus()
    {
        (HttpResponseMessage curl, HttpResponseMessage sockets) =
            await BothAsync(c => c.GetAsync(fixture.Http("/redirect/3")));
        using (curl)
        using (sockets)
        {
            Assert.Equal(sockets.StatusCode, curl.StatusCode);
            Assert.Equal(sockets.RequestMessage!.RequestUri!.AbsolutePath,
                curl.RequestMessage!.RequestUri!.AbsolutePath);
        }
    }

    [Fact]
    public async Task ExpectHeader_MatchesSocketsHandlerDefault()
    {
        static async Task<bool> SawExpect(HttpClient client, Uri uri)
        {
            using HttpResponseMessage response =
                await client.PostAsync(uri, new StringContent(new string('y', 256 * 1024)));
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("Expect", out _);
        }

        Uri uri = fixture.Http("/echo-headers");
        Assert.Equal(await SawExpect(_sockets, uri), await SawExpect(fixture.Client, uri));
    }

    [Fact]
    public async Task RepeatedResponseHeaders_ExposeTheSameValues()
    {
        (HttpResponseMessage curl, HttpResponseMessage sockets) =
            await BothAsync(c => c.GetAsync(fixture.Http("/repeated-headers")));
        using (curl)
        using (sockets)
        {
            Assert.Equal(sockets.Headers.GetValues("Set-Cookie").ToArray(),
                curl.Headers.GetValues("Set-Cookie").ToArray());
            Assert.Equal(sockets.Headers.GetValues("X-Multi").ToArray(),
                curl.Headers.GetValues("X-Multi").ToArray());
        }
    }

    [Fact]
    public async Task NonSuccessStatus_SurfacesIdentically()
    {
        (HttpResponseMessage curl, HttpResponseMessage sockets) =
            await BothAsync(c => c.GetAsync(fixture.Http("/status/418")));
        using (curl)
        using (sockets)
        {
            Assert.Equal(sockets.StatusCode, curl.StatusCode);
        }
    }
}
