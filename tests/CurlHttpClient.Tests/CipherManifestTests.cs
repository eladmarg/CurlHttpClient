using CurlHttp;
using Xunit;

namespace CurlHttp.Tests;

/// <summary>Parsing + classification of the native cipher inventory. Loading
/// the real inventory requires the native DLL (present in test output).</summary>
public class CipherManifestTests
{
    private const string Sample =
        """
        {"openssl_version":"OpenSSL 3.6.3 9 Jun 2026","openssl_version_hex":"0x30600030",
         "ciphers":[
           {"name":"TLS_AES_256_GCM_SHA384","standard_name":"TLS_AES_256_GCM_SHA384","protocol":"TLSv1.3","kx":"any","auth":"any","bits":256,"aead":true,"enabled_default":true},
           {"name":"ECDHE-RSA-AES128-GCM-SHA256","standard_name":"TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256","protocol":"TLSv1.2","kx":"ECDHE","auth":"RSA","bits":128,"aead":true,"enabled_default":true},
           {"name":"ECDHE-ECDSA-AES128-SHA","standard_name":"TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA","protocol":"TLSv1.0","kx":"ECDHE","auth":"ECDSA","bits":128,"aead":false,"enabled_default":true},
           {"name":"ADH-AES256-GCM-SHA384","standard_name":"TLS_DH_anon_WITH_AES_256_GCM_SHA384","protocol":"TLSv1.2","kx":"DHE","auth":"NULL","bits":256,"aead":true,"enabled_default":false},
           {"name":"DHE-DSS-AES256-GCM-SHA384","standard_name":"TLS_DHE_DSS_WITH_AES_256_GCM_SHA384","protocol":"TLSv1.2","kx":"DHE","auth":"DSS","bits":256,"aead":true,"enabled_default":false},
           {"name":"NULL-SHA256","standard_name":"TLS_RSA_WITH_NULL_SHA256","protocol":"TLSv1.2","kx":"RSA","auth":"RSA","bits":0,"aead":false,"enabled_default":false}
         ]}
        """;

    [Fact]
    public void Parse_ReadsVersionAndRows()
    {
        CurlCipherManifest manifest = CurlCipherManifest.Parse(Sample);
        Assert.Equal("OpenSSL 3.6.3 9 Jun 2026", manifest.OpenSslVersion);
        Assert.Equal(6, manifest.Ciphers.Count);
    }

    [Theory]
    [InlineData("TLS_AES_256_GCM_SHA384", CipherTestClassification.Tls13, true)]
    [InlineData("ECDHE-RSA-AES128-GCM-SHA256", CipherTestClassification.CertificateAuthenticated, true)]
    [InlineData("ECDHE-ECDSA-AES128-SHA", CipherTestClassification.CertificateAuthenticated, true)]
    [InlineData("ADH-AES256-GCM-SHA384", CipherTestClassification.RequiresAnonymous, false)]
    [InlineData("DHE-DSS-AES256-GCM-SHA384", CipherTestClassification.RequiresDssCertificate, false)]
    [InlineData("NULL-SHA256", CipherTestClassification.NullEncryption, false)]
    public void Classify_AssignsExactlyOneCategory(
        string name, CipherTestClassification expected, bool testable)
    {
        CurlCipherManifest manifest = CurlCipherManifest.Parse(Sample);
        CurlCipherInfo cipher = manifest.Ciphers.Single(c => c.Name == name);
        Assert.Equal(expected, cipher.Classify());
        Assert.Equal(testable, cipher.IsTestableForHttps);
    }

    [Fact]
    public void RequiredCertificate_MatchesAuthentication()
    {
        CurlCipherManifest manifest = CurlCipherManifest.Parse(Sample);
        Assert.Equal("RSA", manifest.Ciphers.Single(c => c.Name == "ECDHE-RSA-AES128-GCM-SHA256").RequiredCertificate);
        Assert.Equal("ECDSA", manifest.Ciphers.Single(c => c.Name == "ECDHE-ECDSA-AES128-SHA").RequiredCertificate);
        Assert.Equal("any", manifest.Ciphers.Single(c => c.Name == "TLS_AES_256_GCM_SHA384").RequiredCertificate);
    }

    [Fact]
    public void Load_FromNativeBridge_ReflectsPackagedOpenSsl()
    {
        CurlCipherManifest manifest = CurlCipherManifest.Load();
        Assert.StartsWith("OpenSSL 3.", manifest.OpenSslVersion);
        Assert.True(manifest.Ciphers.Count > 50, $"only {manifest.Ciphers.Count} ciphers enumerated");

        // Mandatory suites are present.
        Assert.Contains(manifest.Ciphers, c => c.Name == "TLS_AES_256_GCM_SHA384");
        Assert.Contains(manifest.Ciphers, c => c.Name == "ECDHE-RSA-AES256-GCM-SHA384");
        // 3DES/RC4 are compiled out of this build.
        Assert.DoesNotContain(manifest.Ciphers, c => c.Name.Contains("3DES") || c.Name.Contains("RC4"));
        // All five TLS 1.3 suites are enumerated.
        Assert.Equal(5, manifest.Ciphers.Count(c => c.Protocol == "TLSv1.3"));
    }

    [Fact]
    public void NativeManifest_OpenSslVersion_MatchesRuntimeDiagnostics()
    {
        // The cipher manifest and the OpenSSL-backend proof must never drift.
        CurlCipherManifest manifest = CurlCipherManifest.Load();
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            UseSystemCertificateStore = true,
        });
        CurlHttpClientDiagnostics diag = handler.GetDiagnostics();

        // diag.SslVersion is e.g. "OpenSSL/3.6.3 (Schannel)"; manifest is
        // "OpenSSL 3.6.3 9 Jun 2026". Compare the x.y.z core.
        string manifestCore = manifest.OpenSslVersion.Split(' ')[1];
        Assert.Contains(manifestCore, diag.SslVersion);
        Assert.True(diag.TlsBackendIsOpenSsl);
    }
}
