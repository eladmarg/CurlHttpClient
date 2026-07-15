using System.Net;
using CurlHttp.IntegrationTests.ApiCoverage;
using Xunit;

namespace CurlHttp.IntegrationTests.Methods;

/// <summary>
/// Exercises EVERY HttpClient request-method overload on the target
/// framework against the curl handler. Each test carries [ApiCoverage] tags
/// consumed by the coverage gate.
/// </summary>
[Collection("integration")]
public class ClientMethodOverloadTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;
    private string JsonUrlString => fixture.Http("/json").AbsoluteUri;
    private Uri JsonUri => fixture.Http("/json");
    private static readonly CancellationToken None = CancellationToken.None;

    /* ----------------------------- GetAsync ×8 ----------------------------- */

    [Fact]
    [ApiCoverage("HttpClient.GetAsync(String)")]
    [ApiCoverage("HttpClient.GetAsync(Uri)")]
    public async Task GetAsync_Plain()
    {
        using HttpResponseMessage a = await Client.GetAsync(JsonUrlString);
        using HttpResponseMessage b = await Client.GetAsync(JsonUri);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.GetAsync(String, CancellationToken)")]
    [ApiCoverage("HttpClient.GetAsync(Uri, CancellationToken)")]
    public async Task GetAsync_WithToken()
    {
        using HttpResponseMessage a = await Client.GetAsync(JsonUrlString, None);
        using HttpResponseMessage b = await Client.GetAsync(JsonUri, None);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.GetAsync(String, HttpCompletionOption)")]
    [ApiCoverage("HttpClient.GetAsync(Uri, HttpCompletionOption)")]
    public async Task GetAsync_WithCompletionOption()
    {
        using HttpResponseMessage a = await Client.GetAsync(JsonUrlString, HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains("hello", await a.Content.ReadAsStringAsync());
        using HttpResponseMessage b = await Client.GetAsync(JsonUri, HttpCompletionOption.ResponseContentRead);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    [ApiCoverage("HttpClient.GetAsync(String, HttpCompletionOption, CancellationToken)")]
    [ApiCoverage("HttpClient.GetAsync(Uri, HttpCompletionOption, CancellationToken)")]
    public async Task GetAsync_WithCompletionOptionAndToken()
    {
        using HttpResponseMessage a = await Client.GetAsync(JsonUrlString, HttpCompletionOption.ResponseHeadersRead, None);
        using HttpResponseMessage b = await Client.GetAsync(JsonUri, HttpCompletionOption.ResponseContentRead, None);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    /* ----------------------- GetString/Bytes/Stream ×12 --------------------- */

    [Fact]
    [ApiCoverage("HttpClient.GetStringAsync(String)")]
    [ApiCoverage("HttpClient.GetStringAsync(Uri)")]
    [ApiCoverage("HttpClient.GetStringAsync(String, CancellationToken)")]
    [ApiCoverage("HttpClient.GetStringAsync(Uri, CancellationToken)")]
    public async Task GetStringAsync_AllOverloads()
    {
        Assert.Contains("hello", await Client.GetStringAsync(JsonUrlString));
        Assert.Contains("hello", await Client.GetStringAsync(JsonUri));
        Assert.Contains("hello", await Client.GetStringAsync(JsonUrlString, None));
        Assert.Contains("hello", await Client.GetStringAsync(JsonUri, None));
    }

    [Fact]
    [ApiCoverage("HttpClient.GetByteArrayAsync(String)")]
    [ApiCoverage("HttpClient.GetByteArrayAsync(Uri)")]
    [ApiCoverage("HttpClient.GetByteArrayAsync(String, CancellationToken)")]
    [ApiCoverage("HttpClient.GetByteArrayAsync(Uri, CancellationToken)")]
    public async Task GetByteArrayAsync_AllOverloads()
    {
        Assert.NotEmpty(await Client.GetByteArrayAsync(JsonUrlString));
        Assert.NotEmpty(await Client.GetByteArrayAsync(JsonUri));
        Assert.NotEmpty(await Client.GetByteArrayAsync(JsonUrlString, None));
        Assert.NotEmpty(await Client.GetByteArrayAsync(JsonUri, None));
    }

    [Fact]
    [ApiCoverage("HttpClient.GetStreamAsync(String)")]
    [ApiCoverage("HttpClient.GetStreamAsync(Uri)")]
    [ApiCoverage("HttpClient.GetStreamAsync(String, CancellationToken)")]
    [ApiCoverage("HttpClient.GetStreamAsync(Uri, CancellationToken)")]
    public async Task GetStreamAsync_AllOverloads()
    {
        foreach (Func<Task<Stream>> open in new Func<Task<Stream>>[]
        {
            () => Client.GetStreamAsync(JsonUrlString),
            () => Client.GetStreamAsync(JsonUri),
            () => Client.GetStreamAsync(JsonUrlString, None),
            () => Client.GetStreamAsync(JsonUri, None),
        })
        {
            await using Stream stream = await open();
            using var reader = new StreamReader(stream);
            Assert.Contains("hello", await reader.ReadToEndAsync());
        }
    }

    /* ------------------------- Post/Put/Patch ×12 --------------------------- */

    [Fact]
    [ApiCoverage("HttpClient.PostAsync(String, HttpContent)")]
    [ApiCoverage("HttpClient.PostAsync(Uri, HttpContent)")]
    [ApiCoverage("HttpClient.PostAsync(String, HttpContent, CancellationToken)")]
    [ApiCoverage("HttpClient.PostAsync(Uri, HttpContent, CancellationToken)")]
    public async Task PostAsync_AllOverloads()
    {
        Uri echo = fixture.Http("/echo-method");
        using HttpResponseMessage a = await Client.PostAsync(echo.AbsoluteUri, new StringContent("x"));
        using HttpResponseMessage b = await Client.PostAsync(echo, new StringContent("x"));
        using HttpResponseMessage c = await Client.PostAsync(echo.AbsoluteUri, new StringContent("x"), None);
        using HttpResponseMessage d = await Client.PostAsync(echo, new StringContent("x"), None);
        foreach (HttpResponseMessage response in new[] { a, b, c, d })
        {
            Assert.Equal("POST", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    [ApiCoverage("HttpClient.PutAsync(String, HttpContent)")]
    [ApiCoverage("HttpClient.PutAsync(Uri, HttpContent)")]
    [ApiCoverage("HttpClient.PutAsync(String, HttpContent, CancellationToken)")]
    [ApiCoverage("HttpClient.PutAsync(Uri, HttpContent, CancellationToken)")]
    public async Task PutAsync_AllOverloads()
    {
        Uri echo = fixture.Http("/echo-method");
        using HttpResponseMessage a = await Client.PutAsync(echo.AbsoluteUri, new StringContent("x"));
        using HttpResponseMessage b = await Client.PutAsync(echo, new StringContent("x"));
        using HttpResponseMessage c = await Client.PutAsync(echo.AbsoluteUri, new StringContent("x"), None);
        using HttpResponseMessage d = await Client.PutAsync(echo, new StringContent("x"), None);
        foreach (HttpResponseMessage response in new[] { a, b, c, d })
        {
            Assert.Equal("PUT", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    [ApiCoverage("HttpClient.PatchAsync(String, HttpContent)")]
    [ApiCoverage("HttpClient.PatchAsync(Uri, HttpContent)")]
    [ApiCoverage("HttpClient.PatchAsync(String, HttpContent, CancellationToken)")]
    [ApiCoverage("HttpClient.PatchAsync(Uri, HttpContent, CancellationToken)")]
    public async Task PatchAsync_AllOverloads()
    {
        Uri echo = fixture.Http("/echo-method");
        using HttpResponseMessage a = await Client.PatchAsync(echo.AbsoluteUri, new StringContent("x"));
        using HttpResponseMessage b = await Client.PatchAsync(echo, new StringContent("x"));
        using HttpResponseMessage c = await Client.PatchAsync(echo.AbsoluteUri, new StringContent("x"), None);
        using HttpResponseMessage d = await Client.PatchAsync(echo, new StringContent("x"), None);
        foreach (HttpResponseMessage response in new[] { a, b, c, d })
        {
            Assert.Equal("PATCH", await response.Content.ReadAsStringAsync());
        }
    }

    /* ------------------------------ Delete ×4 ------------------------------- */

    [Fact]
    [ApiCoverage("HttpClient.DeleteAsync(String)")]
    [ApiCoverage("HttpClient.DeleteAsync(Uri)")]
    [ApiCoverage("HttpClient.DeleteAsync(String, CancellationToken)")]
    [ApiCoverage("HttpClient.DeleteAsync(Uri, CancellationToken)")]
    public async Task DeleteAsync_AllOverloads()
    {
        Uri echo = fixture.Http("/echo-method");
        using HttpResponseMessage a = await Client.DeleteAsync(echo.AbsoluteUri);
        using HttpResponseMessage b = await Client.DeleteAsync(echo);
        using HttpResponseMessage c = await Client.DeleteAsync(echo.AbsoluteUri, None);
        using HttpResponseMessage d = await Client.DeleteAsync(echo, None);
        foreach (HttpResponseMessage response in new[] { a, b, c, d })
        {
            Assert.Equal("DELETE", await response.Content.ReadAsStringAsync());
        }
    }

    /* ------------------------------ SendAsync ×4 ---------------------------- */

    [Fact]
    [ApiCoverage("HttpClient.SendAsync(HttpRequestMessage)")]
    [ApiCoverage("HttpClient.SendAsync(HttpRequestMessage, CancellationToken)")]
    [ApiCoverage("HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption)")]
    [ApiCoverage("HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)")]
    public async Task SendAsync_AllOverloads()
    {
        using HttpResponseMessage a = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, JsonUri));
        using HttpResponseMessage b = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, JsonUri), None);
        using HttpResponseMessage c = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, JsonUri), HttpCompletionOption.ResponseHeadersRead);
        using HttpResponseMessage d = await Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, JsonUri), HttpCompletionOption.ResponseHeadersRead, None);
        foreach (HttpResponseMessage response in new[] { a, b, c, d })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
