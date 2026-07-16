using CurlHttp.IntegrationTests.CipherSuites;
using Xunit;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>Exact TLS protocol-version negotiation and min/max policy,
/// verified against a version-pinned openssl s_server.</summary>
[Collection("cipher-suites")]
public class ProtocolVersionMatrixTests(CipherMaterialFixture fixture)
{
    private OpenSslCipherServer StartServer(string versionArg)
        => OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            $"{versionArg} -cipher \"DEFAULT@SECLEVEL=0\" -ciphersuites \"TLS_AES_256_GCM_SHA384\"");

    private HttpClient NewClient(Version? min = null)
        => new(new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            MinimumTlsVersion = min,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }));

    [Fact]
    public async Task Tls12_Server_NegotiatesTls12()
    {
        using OpenSslCipherServer server = StartServer("-tls1_2");
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Contains("Protocol  : TLSv1.2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tls13_Server_NegotiatesTls13()
    {
        using OpenSslCipherServer server = StartServer("-tls1_3");
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Contains("Protocol  : TLSv1.3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClientMinTls13_AgainstTls12Server_FailsHandshake()
    {
        // Client maximum floor above server version → no shared protocol.
        using OpenSslCipherServer server = StartServer("-tls1_2");
        using HttpClient client = NewClient(min: new Version(1, 3));
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(server.BaseUri));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task ClientMinTls12_AgainstTls13Server_Succeeds()
    {
        // Client minimum below server version → negotiates the server's 1.3.
        using OpenSslCipherServer server = StartServer("-tls1_3");
        using HttpClient client = NewClient(min: new Version(1, 2));
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Contains("Protocol  : TLSv1.3", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("-tls1")]  // TLS 1.0
    [InlineData("-tls1_1")] // TLS 1.1
    public async Task LegacyTlsServer_IsRejected_ByTheClientFloor(string legacyVersion)
    {
        // Even with SECLEVEL=0 on the server, the client refuses < TLS 1.2.
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            $"{legacyVersion} -cipher \"DEFAULT@SECLEVEL=0\"");
        using HttpClient client = NewClient();
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(server.BaseUri));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    [Fact]
    public async Task DefaultNegotiation_PrefersTls13_WhenServerSupportsBoth()
    {
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-cipher \"DEFAULT@SECLEVEL=0\""); // no version restriction
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Contains("Protocol  : TLSv1.3", await response.Content.ReadAsStringAsync());
    }
}
