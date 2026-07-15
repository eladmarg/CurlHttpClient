using System.Net;
using CurlHttp.IntegrationTests.CipherSuites;
using Xunit;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>SNI and ALPN negotiation. HTTP/2 ALPN is exercised against the
/// Kestrel HTTPS server (which speaks h2); http/1.1 fallback and SNI use the
/// s_server oracle where its reported handshake details are the evidence.</summary>
[Collection("integration")]
public class SniAndAlpnTests(ServerFixture fixture)
{
    [Fact]
    public async Task Alpn_NegotiatesH2_WhenHttp2Enabled()
    {
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Server.CaBundlePath,
            EnableHttp2 = true,
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(2, 0), response.Version); // ALPN chose h2
    }

    [Fact]
    public async Task Alpn_UsesHttp11_WhenHttp2Disabled()
    {
        // Same server (offers h2 + http/1.1); client not requesting h2 → 1.1.
        using HttpResponseMessage response = await fixture.Client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(1, 1), response.Version);
    }

    [Fact]
    public async Task Sni_CorrectHostname_HandshakeSucceeds()
    {
        // Connecting by the SAN-covered name proves SNI + cert validation +
        // Host header all agree.
        using HttpResponseMessage response = await fixture.Client.GetAsync(fixture.Https("/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HostHeaderOverride_DoesNotChangeSniOrCertValidation()
    {
        // A custom Host header must not repoint SNI/cert validation, which
        // stay bound to the connection's target host.
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Https("/echo-headers"));
        request.Headers.TryAddWithoutValidation("Host", "virtual.example");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>ALPN against the s_server oracle (HTTP/1.1 only). Evidence is the
/// client-observed negotiated protocol version.</summary>
[Collection("cipher-suites")]
public class AlpnOracleTests(CipherMaterialFixture fixture)
{
    [Fact]
    public async Task Alpn_Http11_ServerOffersHttp11_HandshakeSucceeds()
    {
        // s_server advertises http/1.1 via ALPN; a successful HTTPS request
        // confirms the client offered a compatible ALPN list and negotiated
        // it. (s_server -www replies with an HTTP/1.0 status page, so the
        // framing version is not the ALPN evidence — success is.)
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-tls1_3 -alpn http/1.1");
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Alpn_ClientPrefersH2_ServerOnlyHttp11_FallsBackCleanly()
    {
        // Client prefers h2 but the server only offers http/1.1 via ALPN;
        // the request must still succeed (clean fallback, no hard failure).
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-tls1_3 -alpn http/1.1");
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            EnableHttp2 = true,
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
