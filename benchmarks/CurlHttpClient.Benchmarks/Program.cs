using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CurlHttp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

BenchmarkRunner.Run<HandlerBenchmarks>(args: args);

/// <summary>
/// CurlHttpMessageHandler vs SocketsHttpHandler against an in-process Kestrel
/// server (plain HTTP so the comparison measures the handler pipeline, not
/// two different TLS stacks). SocketsHttpHandler is the reference — parity
/// within a small factor is the goal, not beating it: the entire reason this
/// library exists is TLS capability on WS2012R2, not raw throughput.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(2)]
[IterationCount(8)]
public class HandlerBenchmarks
{
    private WebApplication _server = null!;
    private HttpClient _curl = null!;
    private HttpClient _sockets = null!;
    private Uri _json = null!;
    private Uri _large = null!;
    private Uri _upload = null!;
    private byte[] _uploadPayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Listen(IPAddress.Loopback, 0);
        });
        _server = builder.Build();
        _server.MapGet("/json", () => Microsoft.AspNetCore.Http.Results.Json(new { hello = "world", n = 42 }));
        _server.MapGet("/large", async ctx =>
        {
            byte[] block = new byte[81920];
            const long total = 32L * 1024 * 1024;
            ctx.Response.ContentLength = total;
            long remaining = total;
            while (remaining > 0)
            {
                int size = (int)Math.Min(block.Length, remaining);
                await ctx.Response.Body.WriteAsync(block.AsMemory(0, size));
                remaining -= size;
            }
        });
        _server.MapPost("/upload", async ctx =>
        {
            byte[] buffer = new byte[81920];
            while (await ctx.Request.Body.ReadAsync(buffer) > 0)
            {
            }
            await ctx.Response.WriteAsync("ok");
        });
        _server.Start();

        string address = _server.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
        var baseUri = new Uri(address);
        _json = new Uri(baseUri, "/json");
        _large = new Uri(baseUri, "/large");
        _upload = new Uri(baseUri, "/upload");
        _uploadPayload = new byte[8 * 1024 * 1024];

        _curl = new HttpClient(new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            UseSystemCertificateStore = true, // trust source unused for http
        }));
        _sockets = new HttpClient(new SocketsHttpHandler());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _curl.Dispose();
        _sockets.Dispose();
        _server.StopAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public Task<string> SmallJson_Sockets() => _sockets.GetStringAsync(_json);

    [Benchmark]
    public Task<string> SmallJson_Curl() => _curl.GetStringAsync(_json);

    [Benchmark]
    public Task<long> Download32MB_Sockets() => DrainAsync(_sockets, _large);

    [Benchmark]
    public Task<long> Download32MB_Curl() => DrainAsync(_curl, _large);

    [Benchmark]
    public Task Upload8MB_Sockets() => UploadAsync(_sockets);

    [Benchmark]
    public Task Upload8MB_Curl() => UploadAsync(_curl);

    [Benchmark]
    public Task Concurrent32_SmallJson_Sockets() => ConcurrentAsync(_sockets);

    [Benchmark]
    public Task Concurrent32_SmallJson_Curl() => ConcurrentAsync(_curl);

    private static async Task<long> DrainAsync(HttpClient client, Uri uri)
    {
        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }
        return total;
    }

    private async Task UploadAsync(HttpClient client)
    {
        using var content = new ByteArrayContent(_uploadPayload);
        using HttpResponseMessage response = await client.PostAsync(_upload, content);
        response.EnsureSuccessStatusCode();
    }

    private async Task ConcurrentAsync(HttpClient client)
    {
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => client.GetStringAsync(_json)));
    }
}
