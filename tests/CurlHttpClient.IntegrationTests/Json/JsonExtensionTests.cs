using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CurlHttp.IntegrationTests.ApiCoverage;
using Xunit;

namespace CurlHttp.IntegrationTests.Json;

public sealed record JsonDoc(string Name, int Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonDoc))]
public sealed partial class JsonDocContext : JsonSerializerContext;

/// <summary>System.Net.Http.Json extension methods (in-box on net10) over
/// the curl handler — representative overload of every method family plus
/// the serializer-parameter variants (options, JsonTypeInfo, context).</summary>
[Collection("integration")]
public class JsonExtensionTests(ServerFixture fixture)
{
    private HttpClient Client => fixture.Client;
    private static readonly JsonSerializerOptions Web = JsonSerializerOptions.Web;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsync<T>(HttpClient, String, CancellationToken)")]
    public async Task GetFromJsonAsync_String_Generic()
    {
        JsonDoc? doc = await Client.GetFromJsonAsync<JsonDoc>(
            fixture.Http("/json-doc").AbsoluteUri, None);
        Assert.Equal(new JsonDoc("curl", 42), doc);
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsync<T>(HttpClient, Uri, JsonSerializerOptions, CancellationToken)")]
    public async Task GetFromJsonAsync_Uri_WithOptions()
    {
        JsonDoc? doc = await Client.GetFromJsonAsync<JsonDoc>(fixture.Http("/json-doc"), Web, None);
        Assert.Equal(new JsonDoc("curl", 42), doc);
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsync<T>(HttpClient, String, JsonTypeInfo<T>, CancellationToken)")]
    public async Task GetFromJsonAsync_WithTypeInfo_SourceGenerated()
    {
        JsonDoc? doc = await Client.GetFromJsonAsync(
            fixture.Http("/json-doc").AbsoluteUri, JsonDocContext.Default.JsonDoc, None);
        Assert.Equal(new JsonDoc("curl", 42), doc);
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, String, Type, CancellationToken)")]
    public async Task GetFromJsonAsync_NonGeneric_Type()
    {
        object? doc = await Client.GetFromJsonAsync(
            fixture.Http("/json-doc").AbsoluteUri, typeof(JsonDoc), None);
        Assert.Equal(new JsonDoc("curl", 42), Assert.IsType<JsonDoc>(doc));
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsync(HttpClient, Uri, Type, JsonSerializerContext, CancellationToken)")]
    public async Task GetFromJsonAsync_NonGeneric_WithSerializerContext()
    {
        object? doc = await Client.GetFromJsonAsync(
            fixture.Http("/json-doc"), typeof(JsonDoc), JsonDocContext.Default, None);
        Assert.Equal(new JsonDoc("curl", 42), Assert.IsType<JsonDoc>(doc));
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.GetFromJsonAsAsyncEnumerable<T>(HttpClient, String, CancellationToken)")]
    public async Task GetFromJsonAsAsyncEnumerable_StreamsArrayItems()
    {
        var items = new List<JsonDoc>();
        await foreach (JsonDoc? item in Client.GetFromJsonAsAsyncEnumerable<JsonDoc>(
            fixture.Http("/json-array").AbsoluteUri, None))
        {
            items.Add(item!);
        }
        Assert.Equal(5, items.Count);
        Assert.Equal(new JsonDoc("item3", 3), items[2]);
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.PostAsJsonAsync<T>(HttpClient, String, T, CancellationToken)")]
    [ApiCoverage("HttpClientJsonExtensions.PostAsJsonAsync<T>(HttpClient, Uri, T, JsonSerializerOptions, CancellationToken)")]
    public async Task PostAsJsonAsync_RoundTrips()
    {
        var payload = new JsonDoc("posted", 7);
        using HttpResponseMessage a = await Client.PostAsJsonAsync(
            fixture.Http("/json-echo").AbsoluteUri, payload, None);
        Assert.Equal(payload, await a.Content.ReadFromJsonAsync<JsonDoc>());

        using HttpResponseMessage b = await Client.PostAsJsonAsync(
            fixture.Http("/json-echo"), payload, Web, None);
        Assert.Equal(payload, await b.Content.ReadFromJsonAsync<JsonDoc>(Web));
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.PutAsJsonAsync<T>(HttpClient, String, T, CancellationToken)")]
    [ApiCoverage("HttpClientJsonExtensions.PutAsJsonAsync<T>(HttpClient, Uri, T, JsonTypeInfo<T>, CancellationToken)")]
    public async Task PutAsJsonAsync_RoundTrips()
    {
        var payload = new JsonDoc("put", 8);
        using HttpResponseMessage a = await Client.PutAsJsonAsync(
            fixture.Http("/json-echo").AbsoluteUri, payload, None);
        Assert.Equal(payload, await a.Content.ReadFromJsonAsync<JsonDoc>());

        using HttpResponseMessage b = await Client.PutAsJsonAsync(
            fixture.Http("/json-echo"), payload, JsonDocContext.Default.JsonDoc, None);
        Assert.Equal(payload, await b.Content.ReadFromJsonAsync(JsonDocContext.Default.JsonDoc));
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.PatchAsJsonAsync<T>(HttpClient, String, T, CancellationToken)")]
    [ApiCoverage("HttpClientJsonExtensions.PatchAsJsonAsync<T>(HttpClient, Uri, T, CancellationToken)")]
    public async Task PatchAsJsonAsync_RoundTrips()
    {
        var payload = new JsonDoc("patched", 9);
        using HttpResponseMessage a = await Client.PatchAsJsonAsync(
            fixture.Http("/json-echo").AbsoluteUri, payload, None);
        Assert.Equal(payload, await a.Content.ReadFromJsonAsync<JsonDoc>());

        using HttpResponseMessage b = await Client.PatchAsJsonAsync(
            fixture.Http("/json-echo"), payload, None);
        Assert.Equal(payload, await b.Content.ReadFromJsonAsync<JsonDoc>());
    }

    [Fact]
    [ApiCoverage("HttpClientJsonExtensions.DeleteFromJsonAsync<T>(HttpClient, String, CancellationToken)")]
    [ApiCoverage("HttpClientJsonExtensions.DeleteFromJsonAsync<T>(HttpClient, Uri, JsonSerializerOptions, CancellationToken)")]
    public async Task DeleteFromJsonAsync_ReturnsTheDocument()
    {
        JsonDoc? a = await Client.DeleteFromJsonAsync<JsonDoc>(
            fixture.Http("/json-doc").AbsoluteUri, None);
        Assert.Equal(new JsonDoc("curl", 42), a);

        JsonDoc? b = await Client.DeleteFromJsonAsync<JsonDoc>(fixture.Http("/json-doc"), Web, None);
        Assert.Equal(new JsonDoc("curl", 42), b);
    }

    [Fact]
    public async Task ReadFromJsonAsync_OnContent_Works()
    {
        // HttpContentJsonExtensions surface (content-level, outside the
        // HttpClient inventory but part of the JSON usage pattern).
        using HttpResponseMessage response = await Client.GetAsync(fixture.Http("/json-doc"));
        JsonDoc? doc = await response.Content.ReadFromJsonAsync<JsonDoc>();
        Assert.Equal(new JsonDoc("curl", 42), doc);
    }
}
