using CurlHttp.Diagnostics;
using Xunit;

namespace CurlHttp.Tests;

public class OptionsValidationTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new CurlHttpClientOptions().Validate();
    }

    [Fact]
    public void MinimumTls11_IsRejected()
    {
        var options = new CurlHttpClientOptions { MinimumTlsVersion = new Version(1, 1) };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    public void MinimumTls12And13_AreAccepted(int major, int minor)
    {
        new CurlHttpClientOptions { MinimumTlsVersion = new Version(major, minor) }.Validate();
    }

    [Fact]
    public void MissingCaBundleFile_IsRejectedEagerly()
    {
        var options = new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = @"C:\does\not\exist\cacert.pem",
        };
        Assert.Throws<FileNotFoundException>(options.Validate);
    }

    [Fact]
    public void TinyResponseBuffer_IsRejected()
    {
        var options = new CurlHttpClientOptions { MaxResponseBufferBytes = 1024 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void BogusCertificateType_IsRejected()
    {
        var options = new CurlHttpClientOptions { ClientCertificateType = "DER" };
        Assert.Throws<ArgumentException>(options.Validate);
    }
}

public class DiagnosticsParsingTests
{
    private const string SampleJson =
        """
        {"bridge_version":"1.0.0","curl_version":"8.21.0",
         "ssl_version":"OpenSSL/3.6.3 (Schannel)",
         "features":["AsynchDNS","HTTP2","libz","brotli"],
         "protocols":["http","https"]}
        """;

    [Fact]
    public void OpenSslBackend_IsDetected()
    {
        var diag = CurlHttpClientDiagnostics.Parse(SampleJson, @"C:\x\bridge.dll", "bundle.pem");
        Assert.True(diag.TlsBackendIsOpenSsl);
        Assert.True(diag.SupportsHttp2);
        Assert.True(diag.SupportsBrotli);
        Assert.True(diag.SupportsAsyncDns);
        Assert.Equal("8.21.0", diag.CurlVersion);
    }

    [Fact]
    public void SchannelBackend_IsRejected()
    {
        var diag = CurlHttpClientDiagnostics.Parse(
            """{"ssl_version":"Schannel","features":[],"protocols":[]}""", null, "x");
        Assert.False(diag.TlsBackendIsOpenSsl);
    }
}

public class LogRedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer secret", "Authorization: <redacted>")]
    [InlineData("authorization: Basic abc", "authorization: <redacted>")]
    [InlineData("Cookie: session=abc", "Cookie: <redacted>")]
    [InlineData("Set-Cookie: a=1", "Set-Cookie: <redacted>")]
    [InlineData("Content-Type: text/plain", "Content-Type: text/plain")]
    public void HeaderLines_AreRedactedWhenSensitive(string input, string expected)
    {
        Assert.Equal(expected, LogRedaction.RedactHeaderLine(input));
    }

    [Fact]
    public void QueryStrings_AreRedactedFromUrls()
    {
        Assert.Equal("https://h.test/path?<redacted>",
            LogRedaction.RedactUrl(new Uri("https://h.test/path?token=abc"), redactQuery: true));
        Assert.Equal("https://h.test/path?token=abc",
            LogRedaction.RedactUrl(new Uri("https://h.test/path?token=abc"), redactQuery: false));
    }
}
