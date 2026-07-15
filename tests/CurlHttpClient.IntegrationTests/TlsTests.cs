using System.Net;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace CurlHttp.IntegrationTests;

[Collection("integration")]
public class TlsTests(ServerFixture fixture)
{
    [Fact]
    public async Task CustomCa_TrustsTheTestServer()
    {
        using HttpResponseMessage response = await fixture.Client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedCa_IsRejected_WithSecureConnectionError()
    {
        // A CA bundle that does NOT contain the server's issuer.
        (X509Certificate2 unrelatedCa, X509Certificate2 leaf) =
            TestCertificates.CreateChain("unused", "unused.test");
        string bundlePath = TestCertificates.WriteCaBundle(unrelatedCa);
        unrelatedCa.Dispose();
        leaf.Dispose();
        try
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = bundlePath,
            });
            using var client = new HttpClient(handler);

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync(fixture.Https("/json")));
            Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    [Fact]
    public async Task HostnameMismatch_IsRejected()
    {
        // The leaf's SAN covers "localhost" + 127.0.0.1 but NOT [::1]; the
        // chain is trusted, so the only thing failing is name verification.
        Uri mismatchUri = fixture.Server.HttpsHostnameMismatchUri;

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
        });
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri(mismatchUri, "/json")));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task Tls12_Negotiates()
    {
        // Endpoint pinned to TLS 1.2 on the server side.
        var uri = new Uri(fixture.Server.HttpsTls12OnlyUri, "/tls-info");
        using HttpResponseMessage response = await fixture.Client.GetAsync(uri);
        Assert.Equal("Tls12", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tls13_Negotiates_WhenClientRequiresIt()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MinimumTlsVersion = new Version(1, 3),
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response =
            await client.GetAsync(fixture.Https("/tls-info"));
        Assert.Equal("Tls13", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MinTls13_AgainstTls12OnlyServer_FailsHandshake()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MinimumTlsVersion = new Version(1, 3),
        });
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri(fixture.Server.HttpsTls12OnlyUri, "/json")));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task CustomTls12CipherList_IsHonored()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            MinimumTlsVersion = new Version(1, 2),
            Tls12CipherList = "ECDHE-RSA-AES256-GCM-SHA384",
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(fixture.Server.HttpsTls12OnlyUri, "/tls-info"));
        Assert.Equal("Tls12", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Diagnostics_ReportTheActiveTrustSource()
    {
        CurlHttpClientDiagnostics diag = fixture.Handler.GetDiagnostics();
        Assert.Equal(Path.GetFullPath(fixture.Server.CaBundlePath), diag.TrustSource);
    }
}
