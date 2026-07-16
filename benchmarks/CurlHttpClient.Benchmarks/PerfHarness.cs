using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CurlHttp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CurlHttp.Benchmarks;

/// <summary>
/// Precise, fast latency + allocation harness — deterministic per-request
/// measurement via GC.GetAllocatedBytesForCurrentThread and Stopwatch
/// percentiles, run on a single thread to isolate the request pipeline.
/// This is the source of the artifacts/performance baseline/optimized
/// numbers and the regression-test budgets; BDN (HandlerBenchmarks) covers
/// statistical throughput. Run with: dotnet run -c Release -- harness &lt;out.json&gt;
/// </summary>
public static class PerfHarness
{
    private sealed record ScenarioResult(
        string Name, long Iterations, double MedianUs, double P95Us, double P99Us,
        long AllocBytesPerOp, string Notes);

    public static async Task<int> RunAsync(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "perf-harness.json";
        string label = args.Length > 2 ? args[2] : "baseline";

        (X509Certificate2 ca, X509Certificate2 leaf) = CreateChain();
        string caBundle = WriteCaBundle(ca);
        // Production-representative bundle: the test CA (verifies the local
        // leaf) plus the real ~200 KB Mozilla root set, so the new-connection
        // scenario reflects the X509-store parse cost the CA-cache fix targets.
        string realisticBundle = WriteRealisticBundle(ca);
        try
        {
            await using var server = await PerfServer.StartAsync(leaf);
            var results = new List<ScenarioResult>();

            using (var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = caBundle,
            }))
            using (var client = new HttpClient(handler))
            {
                // Warm the connection pool + JIT.
                await Warmup(client, server);

                results.Add(await MeasureAsync("small-reused-get-http", 5000,
                    () => GetDiscardAsync(client, server.HttpJson),
                    "keep-alive reused connection, plain HTTP"));

                results.Add(await MeasureAsync("small-reused-get-https", 5000,
                    () => GetDiscardAsync(client, server.HttpsJson),
                    "keep-alive reused TLS connection"));

                results.Add(await MeasureAsync("ttfb-headers-read", 3000,
                    () => HeadersOnlyAsync(client, server.HttpJson),
                    "ResponseHeadersRead, time to headers"));

                results.Add(await MeasureAsync("small-json-post", 3000,
                    () => PostJsonAsync(client, server.HttpEcho),
                    "small JSON POST round-trip"));

                results.Add(await MeasureAsync("sync-send-get", 3000,
                    () => { SyncGet(client, server.HttpJson); return Task.CompletedTask; },
                    "synchronous HttpClient.Send + buffered read"));

                results.Add(await MeasureAsync("download-32mb", 20,
                    () => DownloadAsync(client, server.HttpLarge),
                    "streamed 32 MB download, alloc/op includes body pipeline"));

                results.Add(await MeasureAsync("upload-8mb", 30,
                    () => UploadAsync(client, server.HttpUpload, 8 * 1024 * 1024),
                    "streamed 8 MB upload"));
            }

            // New-connection HTTPS: fresh TLS handshake each iteration (server
            // sends Connection: close) — this is what the CA-store cache fix
            // targets. Separate handler so pool state is clean.
            using (var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = realisticBundle,
            }))
            using (var client = new HttpClient(handler))
            {
                await Warmup(client, server);
                results.Add(await MeasureAsync("new-connection-https-get", 400,
                    () => GetDiscardAsync(client, server.HttpsClose),
                    "fresh TLS handshake per request (server closes), ~200 KB CA bundle — CA-parse sensitive"));
            }

            // Cancellation latency: time from Cancel() to observed abort.
            results.Add(await MeasureCancellationAsync(caBundle, server));

            // Handler create/dispose cost.
            results.Add(await MeasureAsync("handler-create-dispose", 200,
                () =>
                {
                    var h = new CurlHttpMessageHandler(new CurlHttpClientOptions
                    {
                        CertificateAuthorityBundlePath = caBundle,
                    });
                    h.Dispose();
                    return Task.CompletedTask;
                }, "native client create + dispose"));

            var doc = new
            {
                label,
                capturedUtc = DateTime.UtcNow,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                gcServer = System.Runtime.GCSettings.IsServerGC,
                results,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, JsonSerializer.Serialize(doc,
                new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"{label} harness → {outPath}");
            Console.WriteLine($"{"scenario",-32}{"median µs",12}{"p99 µs",12}{"alloc B/op",14}");
            foreach (ScenarioResult r in results)
            {
                Console.WriteLine($"{r.Name,-32}{r.MedianUs,12:F1}{r.P99Us,12:F1}{r.AllocBytesPerOp,14:N0}");
            }
            return 0;
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(caBundle);
            File.Delete(realisticBundle);
        }
    }

    private static async Task<ScenarioResult> MeasureAsync(
        string name, int iterations, Func<Task> op, string notes)
    {
        // Untimed warm iterations.
        for (int i = 0; i < Math.Min(50, iterations / 10 + 1); i++)
        {
            await op();
        }

        var samples = new double[iterations];
        // Allocation measured over the whole loop on this thread, divided —
        // avoids per-op Stopwatch/array-of-longs noise in the alloc figure.
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            await op();
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Array.Sort(samples);
        return new ScenarioResult(
            name, iterations,
            MedianUs: samples[iterations / 2],
            P95Us: samples[(int)(iterations * 0.95)],
            P99Us: samples[(int)(iterations * 0.99)],
            AllocBytesPerOp: (allocAfter - allocBefore) / iterations,
            Notes: notes);
    }

    private static async Task<ScenarioResult> MeasureCancellationAsync(
        string caBundle, PerfServer server)
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = caBundle,
        });
        using var client = new HttpClient(handler);

        const int iterations = 100;
        var samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            using var cts = new CancellationTokenSource();
            Task<HttpResponseMessage> pending = client.GetAsync(server.HttpDelay, cts.Token);
            await Task.Delay(20);
            long t0 = Stopwatch.GetTimestamp();
            cts.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
            }
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        Array.Sort(samples);
        return new ScenarioResult("cancellation-latency", iterations,
            samples[iterations / 2], samples[95], samples[99], 0,
            "time from Cancel() to observed abort during header wait");
    }

    private static async Task Warmup(HttpClient client, PerfServer server)
    {
        for (int i = 0; i < 20; i++)
        {
            await GetDiscardAsync(client, server.HttpJson);
            await GetDiscardAsync(client, server.HttpsJson);
        }
    }

    private static async Task GetDiscardAsync(HttpClient client, Uri uri)
    {
        using HttpResponseMessage r = await client.GetAsync(uri);
        await r.Content.CopyToAsync(Stream.Null);
    }

    private static async Task HeadersOnlyAsync(HttpClient client, Uri uri)
    {
        using HttpResponseMessage r = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead);
        await r.Content.CopyToAsync(Stream.Null);
    }

    private static async Task PostJsonAsync(HttpClient client, Uri uri)
    {
        using var content = new StringContent("""{"a":1,"b":"two"}""",
            System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage r = await client.PostAsync(uri, content);
        await r.Content.CopyToAsync(Stream.Null);
    }

    private static void SyncGet(HttpClient client, Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage r = client.Send(request);
        using Stream s = r.Content.ReadAsStream();
        s.CopyTo(Stream.Null);
    }

    private static async Task DownloadAsync(HttpClient client, Uri uri)
    {
        using HttpResponseMessage r = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead);
        await using Stream s = await r.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[128 * 1024];
        while (await s.ReadAsync(buffer) > 0)
        {
        }
    }

    private static readonly byte[] UploadBuffer = new byte[8 * 1024 * 1024];

    private static async Task UploadAsync(HttpClient client, Uri uri, int size)
    {
        using var content = new ByteArrayContent(UploadBuffer, 0, size);
        using HttpResponseMessage r = await client.PostAsync(uri, content);
        await r.Content.CopyToAsync(Stream.Null);
    }

    /* ---- test certificate + CA bundle (self-contained) ---- */

    private static (X509Certificate2 Ca, X509Certificate2 Leaf) CreateChain()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN=Perf CA {Guid.NewGuid():N}",
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        X509Certificate2 ca = caReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));

        using RSA leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=localhost", leafKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        X509Certificate2 pub = leafReq.Create(ca, now.AddDays(-1), now.AddDays(7),
            RandomNumberGenerator.GetBytes(12));
        using X509Certificate2 withKey = pub.CopyWithPrivateKey(leafKey);
        X509Certificate2 leaf = X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        pub.Dispose();
        return (ca, leaf);
    }

    private static string WriteCaBundle(X509Certificate2 ca)
    {
        string path = Path.Combine(Path.GetTempPath(), $"perf-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    /// <summary>Test CA + the vendored Mozilla cacert.pem, so the parsed store
    /// is production-sized (~150 certs). The local leaf still verifies via the
    /// test CA; the extra roots only enlarge the parse.</summary>
    private static string WriteRealisticBundle(X509Certificate2 ca)
    {
        string path = Path.Combine(Path.GetTempPath(), $"perf-ca-real-{Guid.NewGuid():N}.pem");
        var sb = new System.Text.StringBuilder(ca.ExportCertificatePem()).AppendLine();

        // Locate the vendored bundle relative to the repo.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "native", "cacert.pem")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        string? mozilla = dir is null ? null : Path.Combine(dir, "native", "cacert.pem");
        if (mozilla is not null && File.Exists(mozilla))
        {
            sb.Append(File.ReadAllText(mozilla));
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}

/// <summary>In-process Kestrel with HTTP + HTTPS endpoints for the harness.</summary>
internal sealed class PerfServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public Uri HttpJson { get; private set; } = null!;
    public Uri HttpsJson { get; private set; } = null!;
    public Uri HttpsClose { get; private set; } = null!;
    public Uri HttpEcho { get; private set; } = null!;
    public Uri HttpLarge { get; private set; } = null!;
    public Uri HttpUpload { get; private set; } = null!;
    public Uri HttpDelay { get; private set; } = null!;

    private PerfServer(WebApplication app) => _app = app;

    public static async Task<PerfServer> StartAsync(X509Certificate2 serverCert)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Listen(IPAddress.Loopback, 0);
            k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(serverCert));
        });
        WebApplication app = builder.Build();

        app.MapGet("/json", () => Results.Json(new { hello = "world", n = 42 }));
        app.MapGet("/close", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Connection = "close";
            return Results.Json(new { hello = "world" });
        });
        app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(body);
        });
        app.MapGet("/large", async ctx =>
        {
            byte[] block = new byte[81920];
            const long total = 32L * 1024 * 1024;
            ctx.Response.ContentLength = total;
            long remaining = total;
            while (remaining > 0)
            {
                int n = (int)Math.Min(block.Length, remaining);
                await ctx.Response.Body.WriteAsync(block.AsMemory(0, n));
                remaining -= n;
            }
        });
        app.MapPost("/upload", async ctx =>
        {
            byte[] buffer = new byte[81920];
            while (await ctx.Request.Body.ReadAsync(buffer) > 0)
            {
            }
            await ctx.Response.WriteAsync("ok");
        });
        app.MapGet("/delay", async ctx => await Task.Delay(30000, ctx.RequestAborted));

        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.Select(a => new Uri(a)).ToList();
        Uri http = addresses.First(u => u.Scheme == "http");
        Uri https = addresses.First(u => u.Scheme == "https");

        return new PerfServer(app)
        {
            HttpJson = new Uri(http, "/json"),
            HttpsJson = new Uri(https, "/json"),
            HttpsClose = new Uri(https, "/close"),
            HttpEcho = new Uri(http, "/echo"),
            HttpLarge = new Uri(http, "/large"),
            HttpUpload = new Uri(http, "/upload"),
            HttpDelay = new Uri(http, "/delay"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
