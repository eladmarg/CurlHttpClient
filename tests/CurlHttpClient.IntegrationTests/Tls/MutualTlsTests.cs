using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CurlHttp.IntegrationTests.CipherSuites;
using Xunit;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>Mutual TLS: client certificate presentation in PEM and PKCS#12
/// forms (incl. password-protected key) across TLS 1.2 and 1.3.</summary>
[Collection("cipher-suites")]
public class MutualTlsTests(CipherMaterialFixture fixture) : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"mtls-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (string CertPem, string KeyPem, string Pfx, string PfxPassword, X509Certificate2 ClientCa)
        CreateClientCredentials()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN=Client CA {Guid.NewGuid():N}",
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        X509Certificate2 clientCa = caReq.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        using RSA clientKey = RSA.Create(2048);
        var clientReq = new CertificateRequest("CN=curl-mtls-client",
            clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth
        using X509Certificate2 clientPub = clientReq.Create(clientCa,
            now.AddDays(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(12));

        string certPem = Path.Combine(_dir, "client.pem");
        string keyPem = Path.Combine(_dir, "client.key.pem");
        string pfx = Path.Combine(_dir, "client.pfx");
        const string password = "test-password-not-a-secret";
        File.WriteAllText(certPem, clientPub.ExportCertificatePem());
        File.WriteAllText(keyPem, clientKey.ExportPkcs8PrivateKeyPem());
        using (X509Certificate2 withKey = clientPub.CopyWithPrivateKey(clientKey))
        {
            File.WriteAllBytes(pfx, withKey.Export(X509ContentType.Pkcs12, password));
        }
        return (certPem, keyPem, pfx, password, clientCa);
    }

    private OpenSslCipherServer StartServerRequiringClientCert(
        X509Certificate2 clientCa, string versionArg)
    {
        string clientCaPath = Path.Combine(_dir, "client-ca.pem");
        File.WriteAllText(clientCaPath, clientCa.ExportCertificatePem());
        // -Verify 1 requires a client cert; -CAfile trusts our client CA.
        return OpenSslCipherServer.StartWithArgs(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            $"{versionArg} -Verify 1 -CAfile \"{clientCaPath}\"");
    }

    [Theory]
    [InlineData("-tls1_2")]
    [InlineData("-tls1_3")]
    public async Task PemClientCertificate_IsAccepted(string version)
    {
        var creds = CreateClientCredentials();
        using (creds.ClientCa)
        using (OpenSslCipherServer server = StartServerRequiringClientCert(creds.ClientCa, version))
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ClientCertificatePath = creds.CertPem,
                ClientCertificateKeyPath = creds.KeyPem,
            });
            using var client = new HttpClient(handler);
            using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Pkcs12ClientCertificate_WithPassword_IsAccepted()
    {
        var creds = CreateClientCredentials();
        using (creds.ClientCa)
        using (OpenSslCipherServer server = StartServerRequiringClientCert(creds.ClientCa, "-tls1_2"))
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ClientCertificatePath = creds.Pfx,
                ClientCertificateType = "P12",
                ClientCertificateKeyPassword = creds.PfxPassword,
            });
            using var client = new HttpClient(handler);
            using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task MissingClientCertificate_WhenServerRequiresOne_Fails()
    {
        var creds = CreateClientCredentials();
        using (creds.ClientCa)
        using (OpenSslCipherServer server = StartServerRequiringClientCert(creds.ClientCa, "-tls1_2"))
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                // no client certificate configured
            });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.BaseUri));
        }
    }

    [Fact]
    public async Task Pkcs12ClientCertificate_WrongPassword_FailsAtConfiguration()
    {
        var creds = CreateClientCredentials();
        using (creds.ClientCa)
        using (OpenSslCipherServer server = StartServerRequiringClientCert(creds.ClientCa, "-tls1_2"))
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ClientCertificatePath = creds.Pfx,
                ClientCertificateType = "P12",
                ClientCertificateKeyPassword = "wrong-password",
            });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.BaseUri));
        }
    }
}
