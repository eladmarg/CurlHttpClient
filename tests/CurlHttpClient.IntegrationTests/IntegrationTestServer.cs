using System.IO.Compression;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CurlHttp.IntegrationTests;

/// <summary>
/// In-process Kestrel server exercising every behaviour the handler must
/// support: streaming, delays, redirects, compression, repeated headers,
/// trailers, uploads, and TLS 1.2/1.3 endpoints with a test CA.
/// </summary>
public sealed class IntegrationTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public Uri HttpBaseUri { get; private set; } = null!;
    public Uri HttpsBaseUri { get; private set; } = null!;
    public Uri HttpsTls12OnlyUri { get; private set; } = null!;
    public Uri HttpsHostnameMismatchUri { get; private set; } = null!;
    public X509Certificate2 CaCertificate { get; }
    public string CaBundlePath { get; }

    private IntegrationTestServer(WebApplication app, X509Certificate2 ca, string caBundlePath)
    {
        _app = app;
        CaCertificate = ca;
        CaBundlePath = caBundlePath;
    }

    public static async Task<IntegrationTestServer> StartAsync()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) =
            TestCertificates.CreateChain("localhost", "localhost");
        string caBundlePath = TestCertificates.WriteCaBundle(ca);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(System.Net.IPAddress.Loopback, 0); // HTTP
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = leaf;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = leaf;
                https.SslProtocols = SslProtocols.Tls12;
            }));
            // IPv6 loopback endpoint: [::1] is deliberately NOT in the leaf's
            // SAN, giving the hostname-mismatch test a trusted-chain,
            // wrong-name target.
            kestrel.Listen(System.Net.IPAddress.IPv6Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = leaf;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
        });

        WebApplication app = builder.Build();
        MapEndpoints(app);
        await app.StartAsync();

        var addresses = app.Urls.Count > 0
            ? app.Urls
            : app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses;
        var list = addresses.Select(a => new Uri(a)).ToList();
        var httpsIpv4 = list.Where(u => u.Scheme == "https" && !u.Host.Contains(':')).ToList();

        var server = new IntegrationTestServer(app, ca, caBundlePath)
        {
            HttpBaseUri = list.First(u => u.Scheme == "http"),
            HttpsBaseUri = httpsIpv4[0],
            HttpsTls12OnlyUri = httpsIpv4[1],
            HttpsHostnameMismatchUri = list.First(u => u.Scheme == "https" && u.Host.Contains(':')),
        };
        return server;
    }

    private static void MapEndpoints(WebApplication app)
    {
        // Every response reveals its transport connection so tests can prove
        // keep-alive reuse.
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Connection-Id"] = context.Connection.Id;
            await next();
        });

        app.MapMethods("/json", ["GET", "HEAD"], () => Results.Json(new { hello = "world" }));

        // JSON-extension test endpoints.
        app.MapMethods("/json-doc", ["GET", "DELETE"],
            () => Results.Json(new { name = "curl", value = 42 }));
        app.MapMethods("/json-echo", ["POST", "PUT", "PATCH"], async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        app.MapGet("/json-array", () => Results.Json(
            Enumerable.Range(1, 5).Select(i => new { name = $"item{i}", value = i })));

        app.MapGet("/slow-body", async (HttpContext context, int chunks, int delayMs) =>
        {
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.FlushAsync();
            byte[] chunk = Encoding.ASCII.GetBytes(new string('x', 1024));
            for (int i = 0; i < chunks; i++)
            {
                await Task.Delay(delayMs, context.RequestAborted);
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });

        app.MapGet("/delayed-headers", async (HttpContext context, int ms) =>
        {
            await Task.Delay(ms, context.RequestAborted);
            return Results.Text("finally");
        });

        app.MapGet("/large", async (HttpContext context, long bytes) =>
        {
            context.Response.ContentLength = bytes;
            byte[] block = new byte[81920];
            new Random(7).NextBytes(block);
            long remaining = bytes;
            while (remaining > 0)
            {
                int size = (int)Math.Min(block.Length, remaining);
                await context.Response.Body.WriteAsync(block.AsMemory(0, size), context.RequestAborted);
                remaining -= size;
            }
        });

        app.MapMethods("/upload", ["POST", "PUT", "PATCH", "PROPFIND", "REPORT"], async (HttpContext context) =>
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
            }
            return Results.Json(new
            {
                bytes = total,
                declaredLength = context.Request.ContentLength,
                transferEncoding = context.Request.Headers.TransferEncoding.ToString(),
            });
        });

        app.MapGet("/redirect/{n:int}", (int n) =>
            Results.Redirect(n > 0 ? $"/redirect/{n - 1}" : "/json", permanent: false));

        app.MapPost("/redirect-post", () => Results.Redirect("/echo-method", permanent: false));

        app.Map("/echo-method", (HttpContext context) =>
            Results.Text(context.Request.Method));

        app.Map("/echo-headers", (HttpContext context) =>
        {
            var headers = context.Request.Headers.ToDictionary(
                h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            return Results.Json(headers);
        });

        app.MapGet("/repeated-headers", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "first=1; Path=/");
            context.Response.Headers.Append("Set-Cookie", "second=2; Path=/; HttpOnly");
            context.Response.Headers.Append("X-Multi", "a");
            context.Response.Headers.Append("X-Multi", "b");
            return Results.Text("ok");
        });

        app.MapGet("/gzip", (HttpContext context) =>
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                """{"compressed":true,"padding":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                gzip.Write(payload);
            }
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(buffer.ToArray());
        });

        app.MapGet("/never", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/plain";
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("started"));
            await context.Response.Body.FlushAsync();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });

        app.MapGet("/trailers", async (HttpContext context) =>
        {
            context.Response.DeclareTrailer("X-Checksum");
            await context.Response.WriteAsync("chunked body");
            context.Response.AppendTrailer("X-Checksum", "abc123");
        });

        app.MapGet("/tls-info", (HttpContext context) =>
        {
            ITlsHandshakeFeature? tls = context.Features.Get<ITlsHandshakeFeature>();
            return Results.Text(tls?.Protocol.ToString() ?? "none");
        });

        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        CaCertificate.Dispose();
        try
        {
            File.Delete(CaBundlePath);
        }
        catch (IOException)
        {
        }
    }
}
