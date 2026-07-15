using System.Text.Json;

namespace CurlHttp;

/// <summary>
/// Snapshot of the native bridge build, exposed through
/// <see cref="CurlHttpMessageHandler.GetDiagnostics"/>. Use
/// <see cref="TlsBackendIsOpenSsl"/> in health checks to programmatically
/// prove the handler is NOT using Schannel.
/// </summary>
public sealed record CurlHttpClientDiagnostics
{
    public required string BridgeVersion { get; init; }
    public required string CurlVersion { get; init; }

    /// <summary>Active TLS backend string, e.g. "OpenSSL/3.6.3 (Schannel)".
    /// The first entry is the backend in use; parenthesized entries are
    /// compiled-in alternates that this bridge pins away from.</summary>
    public required string SslVersion { get; init; }

    public required IReadOnlyList<string> Features { get; init; }
    public required IReadOnlyList<string> Protocols { get; init; }

    /// <summary>True when the ACTIVE TLS backend is OpenSSL.</summary>
    public bool TlsBackendIsOpenSsl => SslVersion.StartsWith("OpenSSL", StringComparison.Ordinal);

    public bool SupportsHttp2 => Features.Contains("HTTP2");
    public bool SupportsBrotli => Features.Contains("brotli");
    public bool SupportsAsyncDns => Features.Contains("AsynchDNS");

    /// <summary>Full path curl_http_bridge.dll was loaded from.</summary>
    public required string? NativeLibraryPath { get; init; }

    /// <summary>Trust source in effect: the CA bundle path, "(embedded cacert.pem)",
    /// and/or "(windows certificate store)".</summary>
    public required string TrustSource { get; init; }

    public required string ProcessArchitecture { get; init; }

    internal static CurlHttpClientDiagnostics Parse(
        string versionJson, string? nativeLibraryPath, string trustSource)
    {
        using JsonDocument doc = JsonDocument.Parse(versionJson);
        JsonElement root = doc.RootElement;

        static IReadOnlyList<string> ReadArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var list = new List<string>();
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.GetString() is { } s)
                {
                    list.Add(s);
                }
            }
            return list;
        }

        static string ReadString(JsonElement root, string name)
            => root.TryGetProperty(name, out JsonElement el) ? el.GetString() ?? string.Empty : string.Empty;

        return new CurlHttpClientDiagnostics
        {
            BridgeVersion = ReadString(root, "bridge_version"),
            CurlVersion = ReadString(root, "curl_version"),
            SslVersion = ReadString(root, "ssl_version"),
            Features = ReadArray(root, "features"),
            Protocols = ReadArray(root, "protocols"),
            NativeLibraryPath = nativeLibraryPath,
            TrustSource = trustSource,
            ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }
}
