using System.Net;
using CurlHttp.IntegrationTests.CipherSuites;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>TLS session resumption evidence: the SSL_SESSION share makes a
/// cached session available to a second connection. Evidence is curl's own
/// verbose TLS log ("SSL reusing session"), captured client-side.</summary>
[Collection("cipher-suites")]
public class SessionResumptionTests(CipherMaterialFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task SecondConnection_ResumesTheTlsSession()
    {
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath, "-tls1_2");

        var logger = new CapturingLogger<CurlHttpMessageHandler>();
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            EnableNativeVerboseLogging = true,
        }, logger);
        using var client = new HttpClient(handler);

        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        output.WriteLine(string.Join("\n", logger.Lines.Where(l => l.Contains("curl"))));
        // curl logs "SSL reusing session" (or "SSL re-using session") once the
        // shared SSL_SESSION cache serves a subsequent handshake.
        Assert.True(logger.Contains("reusing session") || logger.Contains("re-using session"),
            "curl never reported TLS session resumption across the four requests");
    }
}
