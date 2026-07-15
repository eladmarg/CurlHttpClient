using System.Security.Authentication;
using Xunit;

namespace CurlHttp.IntegrationTests.CipherSuites;

/// <summary>
/// The cipher matrix validated against the OPERATING SYSTEM's own TLS stack
/// (SslStream/Schannel) with its default cipher configuration — the exact
/// implementation IIS/Kestrel peers run in production. Mirror image of the
/// OpenSSL-server tests: there the SERVER is pinned per suite; here the
/// CLIENT is pinned (via Tls12CipherList / Tls13CipherSuites) and the server
/// reports which suite Schannel actually negotiated.
///
/// Assumes a modern OS where these suites are enabled (the test environment
/// contract for this repo).
/// </summary>
[Collection("cipher-suites")]
public class SchannelCipherSuiteTests(CipherMaterialFixture fixture)
{
    /// <summary>IANA name (== SslStream.NegotiatedCipherSuite.ToString()),
    /// OpenSSL client-pin name, TLS 1.3 flag, needs-ECDSA-cert flag.</summary>
    public static TheoryData<string, string, bool, bool> SupportedSuites => new()
    {
        { "TLS_AES_256_GCM_SHA384", "TLS_AES_256_GCM_SHA384", true, false },
        { "TLS_AES_128_GCM_SHA256", "TLS_AES_128_GCM_SHA256", true, false },
        { "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", "ECDHE-ECDSA-AES256-GCM-SHA384", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", "ECDHE-ECDSA-AES128-GCM-SHA256", false, true },
        { "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", "ECDHE-RSA-AES256-GCM-SHA384", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", "ECDHE-RSA-AES128-GCM-SHA256", false, false },
        // DHE_RSA is deliberately absent here: current Windows 11 builds ship
        // with all TLS_DHE_RSA_* suites REMOVED from Schannel's default list
        // (verify with Get-TlsCipherSuite). The handler's DHE support is
        // proven by the OpenSSL-server matrix; the Schannel-side behaviour is
        // covered by DheOnlyClient_FailsCleanly_WhenTheOsDisablesDhe below.
        { "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384", "ECDHE-ECDSA-AES256-SHA384", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256", "ECDHE-ECDSA-AES128-SHA256", false, true },
        { "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384", "ECDHE-RSA-AES256-SHA384", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256", "ECDHE-RSA-AES128-SHA256", false, false },
        { "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", "ECDHE-ECDSA-AES256-SHA", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", "ECDHE-ECDSA-AES128-SHA", false, true },
        { "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA", "ECDHE-RSA-AES256-SHA", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", "ECDHE-RSA-AES128-SHA", false, false },
        { "TLS_RSA_WITH_AES_256_GCM_SHA384", "AES256-GCM-SHA384", false, false },
        { "TLS_RSA_WITH_AES_128_GCM_SHA256", "AES128-GCM-SHA256", false, false },
        { "TLS_RSA_WITH_AES_256_CBC_SHA256", "AES256-SHA256", false, false },
        { "TLS_RSA_WITH_AES_128_CBC_SHA256", "AES128-SHA256", false, false },
        { "TLS_RSA_WITH_AES_256_CBC_SHA", "AES256-SHA", false, false },
        { "TLS_RSA_WITH_AES_128_CBC_SHA", "AES128-SHA", false, false },
    };

    [Theory]
    [MemberData(nameof(SupportedSuites))]
    public async Task PinnedClient_NegotiatesTheExactSuite_AgainstTheOsTlsStack(
        string ianaName, string opensslPin, bool tls13, bool needsEcdsaCert)
    {
        using var server = new SslStreamCipherServer(
            needsEcdsaCert ? fixture.Material.EcdsaCertificate : fixture.Material.RsaCertificate,
            tls13 ? SslProtocols.Tls13 : SslProtocols.Tls12);

        var options = new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Pin the CLIENT to exactly one suite; the OS server picks from
            // its defaults — agreement is only possible on that suite.
            Tls12CipherList = tls13 ? null : opensslPin,
            Tls13CipherSuites = tls13 ? ianaName : null,
            MinimumTlsVersion = tls13 ? new Version(1, 3) : null,
        };
        using var handler = new CurlHttpMessageHandler(options);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        string negotiated = await response.Content.ReadAsStringAsync();

        Assert.Equal(ianaName, negotiated);
    }

    [Fact]
    public async Task DheOnlyClient_FailsCleanly_WhenTheOsDisablesDhe()
    {
        // Windows 11 removed TLS_DHE_RSA_* from Schannel defaults. A client
        // that insists on DHE against such a server must fail with a clean,
        // well-mapped handshake error — never hang or misreport.
        using var server = new SslStreamCipherServer(
            fixture.Material.RsaCertificate, SslProtocols.Tls12);

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            Tls12CipherList = "DHE-RSA-AES256-GCM-SHA384:DHE-RSA-AES128-GCM-SHA256",
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(server.BaseUri));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }
}
