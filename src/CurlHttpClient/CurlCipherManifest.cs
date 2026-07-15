using System.Text.Json;
using CurlHttp.Internal;
using CurlHttp.Native;

namespace CurlHttp;

/// <summary>How a discovered cipher suite relates to certificate-authenticated
/// HTTPS through this handler.</summary>
public enum CipherTestClassification
{
    /// <summary>TLS 1.3 suite — usable with any certificate type.</summary>
    Tls13,

    /// <summary>TLS 1.2 (or earlier) suite authenticated by an RSA or ECDSA
    /// certificate — testable for normal HTTPS.</summary>
    CertificateAuthenticated,

    /// <summary>Anonymous key exchange (aNULL) — no certificate, never used
    /// for authenticated HTTPS.</summary>
    RequiresAnonymous,

    /// <summary>NULL bulk cipher (eNULL) — no encryption, out of scope.</summary>
    NullEncryption,

    /// <summary>DSS/DSA authentication — no DSS certificates in scope.</summary>
    RequiresDssCertificate,

    /// <summary>PSK/SRP or another mode outside certificate HTTPS.</summary>
    OutOfScope,
}

/// <summary>One cipher suite as reported by the packaged OpenSSL build.</summary>
public sealed record CurlCipherInfo(
    string Name,
    string StandardName,
    string Protocol,
    string KeyExchange,
    string Authentication,
    int Bits,
    bool IsAead,
    bool EnabledByDefault)
{
    /// <summary>The certificate type this suite's server authentication
    /// requires ("RSA", "ECDSA", "any" for TLS 1.3, or null when none).</summary>
    public string? RequiredCertificate => Authentication switch
    {
        "RSA" => "RSA",
        "ECDSA" => "ECDSA",
        "any" => "any",
        _ => null,
    };

    public CipherTestClassification Classify()
    {
        if (Protocol == "TLSv1.3")
        {
            return CipherTestClassification.Tls13;
        }
        if (Bits == 0)
        {
            return CipherTestClassification.NullEncryption;
        }
        return Authentication switch
        {
            "RSA" or "ECDSA" => CipherTestClassification.CertificateAuthenticated,
            "NULL" => CipherTestClassification.RequiresAnonymous,
            "DSS" => CipherTestClassification.RequiresDssCertificate,
            _ => CipherTestClassification.OutOfScope,
        };
    }

    /// <summary>True when this suite can be exercised against an RSA or ECDSA
    /// server certificate over ordinary HTTPS.</summary>
    public bool IsTestableForHttps =>
        Classify() is CipherTestClassification.Tls13
                   or CipherTestClassification.CertificateAuthenticated;
}

/// <summary>
/// The complete cipher-suite inventory of the statically linked OpenSSL,
/// enumerated through OpenSSL's own API inside the native bridge (so it can
/// never drift from the shipped binary). Drives the cipher test matrix.
/// </summary>
public sealed class CurlCipherManifest
{
    private CurlCipherManifest(string opensslVersion, string opensslVersionHex,
        IReadOnlyList<CurlCipherInfo> ciphers)
    {
        OpenSslVersion = opensslVersion;
        OpenSslVersionHex = opensslVersionHex;
        Ciphers = ciphers;
    }

    /// <summary>e.g. "OpenSSL 3.6.3 9 Jun 2026".</summary>
    public string OpenSslVersion { get; }
    public string OpenSslVersionHex { get; }
    public IReadOnlyList<CurlCipherInfo> Ciphers { get; }

    /// <summary>Enumerates the packaged OpenSSL build's cipher suites.</summary>
    public static CurlCipherManifest Load()
    {
        NativeLibraryLoader.RegisterResolver();
        if (NativeMethods.GlobalInitialize() != CurlBridgeResult.Ok)
        {
            throw new CurlHttpInitializationException(
                $"Native bridge initialization failed: {NativeMethods.GetLastGlobalError()}");
        }
        return Parse(NativeMethods.GetCipherInventoryJson());
    }

    internal static CurlCipherManifest Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var ciphers = new List<CurlCipherInfo>();
        if (root.TryGetProperty("ciphers", out JsonElement array))
        {
            foreach (JsonElement c in array.EnumerateArray())
            {
                ciphers.Add(new CurlCipherInfo(
                    c.GetProperty("name").GetString()!,
                    c.GetProperty("standard_name").GetString()!,
                    c.GetProperty("protocol").GetString()!,
                    c.GetProperty("kx").GetString()!,
                    c.GetProperty("auth").GetString()!,
                    c.GetProperty("bits").GetInt32(),
                    c.GetProperty("aead").GetBoolean(),
                    c.GetProperty("enabled_default").GetBoolean()));
            }
        }
        return new CurlCipherManifest(
            root.TryGetProperty("openssl_version", out JsonElement v) ? v.GetString()! : "",
            root.TryGetProperty("openssl_version_hex", out JsonElement h) ? h.GetString()! : "",
            ciphers);
    }
}
