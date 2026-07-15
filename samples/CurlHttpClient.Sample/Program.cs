// Acceptance-criteria demo: standard HttpClient usage over the libcurl/OpenSSL
// bridge, streaming the response with ResponseHeadersRead, plus the runtime
// proof of which TLS stack is in use.
//
//   dotnet run --project samples/CurlHttpClient.Sample [url]

using CurlHttp;

string url = args.Length > 0 ? args[0] : "https://www.howsmyssl.com/a/check";

var handler = new CurlHttpMessageHandler(
    new CurlHttpClientOptions
    {
        // cacert.pem is resolved automatically from the deployed native
        // assets; set CertificateAuthorityBundlePath to override.
        ConnectTimeout = TimeSpan.FromSeconds(15),
        RequestTimeout = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = true,
        AutomaticDecompression = true,
    });

CurlHttpClientDiagnostics diagnostics = handler.GetDiagnostics();
Console.WriteLine($"bridge          : {diagnostics.BridgeVersion}");
Console.WriteLine($"libcurl         : {diagnostics.CurlVersion}");
Console.WriteLine($"TLS backend     : {diagnostics.SslVersion}  (OpenSSL: {diagnostics.TlsBackendIsOpenSsl})");
Console.WriteLine($"features        : {string.Join(", ", diagnostics.Features)}");
Console.WriteLine($"trust source    : {diagnostics.TrustSource}");
Console.WriteLine($"native library  : {diagnostics.NativeLibraryPath}");
Console.WriteLine();

if (!diagnostics.TlsBackendIsOpenSsl)
{
    Console.Error.WriteLine("FATAL: not running on OpenSSL — refusing to continue.");
    return 1;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var client = new HttpClient(handler);

using var request = new HttpRequestMessage(HttpMethod.Get, url);
using var response = await client.SendAsync(
    request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

Console.WriteLine($"{(int)response.StatusCode} {response.ReasonPhrase} (HTTP/{response.Version})");
foreach (var header in response.Headers)
{
    Console.WriteLine($"  {header.Key}: {string.Join(", ", header.Value)}");
}

response.EnsureSuccessStatusCode();

await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
using var reader = new StreamReader(stream);
string body = await reader.ReadToEndAsync(cancellation.Token);
Console.WriteLine();
Console.WriteLine(body.Length > 2000 ? body[..2000] + "…" : body);
return 0;
