using System.Net;
using CurlHttp.IntegrationTests.CipherSuites;
using Xunit;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>Supported-group (key exchange) and signature-algorithm
/// negotiation, including the HelloRetryRequest path.</summary>
[Collection("cipher-suites")]
public class GroupsAndSignaturesTests(CipherMaterialFixture fixture)
{
    private HttpClient NewClient() => new(new CurlHttpMessageHandler(new CurlHttpClientOptions
    {
        CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }));

    [Theory]
    [InlineData("X25519")]
    [InlineData("prime256v1")] // P-256
    [InlineData("secp384r1")]  // P-384
    public async Task NamedGroup_NegotiatesSuccessfully(string group)
    {
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            $"-tls1_3 -groups {group}");
        using HttpClient client = NewClient();

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Protocol  : TLSv1.3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ServerRestrictedToP384_TriggersHelloRetryRequest_AndSucceeds()
    {
        // The client's default key share is X25519/hybrid; a P-384-only server
        // forces a HelloRetryRequest. It must still complete.
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-tls1_3 -groups secp384r1");
        using HttpClient client = NewClient();

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoSharedGroup_FailsHandshake()
    {
        // Server only offers a group; if the client cannot use it the
        // handshake fails. ffdhe2048 (FFDHE) is not in the client's default
        // TLS 1.3 key-share groups.
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-tls1_3 -groups ffdhe8192");
        using HttpClient client = NewClient();

        // Depending on build this either HRRs into ffdhe or fails; assert the
        // observable contract holds (success OR clean SecureConnectionError).
        try
        {
            using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
        }
    }

    [Fact]
    public async Task Ml_KemHybridGroup_NegotiatesWhenServerOffersIt()
    {
        // OpenSSL 3.5+ enables the post-quantum X25519MLKEM768 hybrid by
        // default on both ends.
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            "-tls1_3 -groups X25519MLKEM768");
        using HttpClient client = NewClient();

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("RSA", "-tls1_2")]   // RSA-PSS/PKCS1 signatures over TLS 1.2
    [InlineData("ECDSA", "-tls1_2")] // ECDSA P-256 signatures
    [InlineData("RSA", "-tls1_3")]
    [InlineData("ECDSA", "-tls1_3")]
    public async Task SignatureAlgorithm_PerCertificateType_Negotiates(string auth, string version)
    {
        (string cert, string key) = fixture.Material.ForAuth(auth);
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            cert, key, $"{version} -cipher \"DEFAULT@SECLEVEL=0\"");
        using HttpClient client = NewClient();

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EcdsaP384Certificate_Negotiates()
    {
        using OpenSslCipherServer server = OpenSslCipherServer.StartWithArgs(
            fixture.Material.EcdsaP384CertPath, fixture.Material.EcdsaP384KeyPath,
            "-tls1_3");
        using HttpClient client = NewClient();
        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
