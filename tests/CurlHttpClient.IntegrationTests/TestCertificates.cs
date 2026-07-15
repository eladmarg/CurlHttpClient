using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CurlHttp.IntegrationTests;

/// <summary>Generates a throwaway CA + server leaf certificate per test run.</summary>
internal static class TestCertificates
{
    public static (X509Certificate2 Ca, X509Certificate2 Leaf) CreateChain(
        string leafCommonName, params string[] dnsNames)
    {
        // One clock read for the whole chain, with the leaf strictly inside
        // the CA's validity. Re-reading UtcNow per certificate intermittently
        // produced leaf.NotAfter one second past ca.NotAfter (key generation
        // straddling a second boundary), which CertificateRequest.Create
        // rejects — the source of rare one-in-many test-run failures.
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset caNotAfter = notBefore.AddDays(31);
        DateTimeOffset leafNotAfter = notBefore.AddDays(30);

        using RSA caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            $"CN=CurlHttpClient Test CA {Guid.NewGuid():N}",
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        X509Certificate2 ca = caRequest.CreateSelfSigned(notBefore, caNotAfter);

        using RSA leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            $"CN={leafCommonName}",
            leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        foreach (string dns in dnsNames)
        {
            san.AddDnsName(dns);
        }
        san.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(san.Build());

        byte[] serial = RandomNumberGenerator.GetBytes(12);
        X509Certificate2 leafPublic = leafRequest.Create(ca, notBefore, leafNotAfter, serial);

        // Round-trip through PKCS#12 so Schannel (Kestrel server side) gets a
        // persisted rather than ephemeral private key.
        using X509Certificate2 leafWithKey = leafPublic.CopyWithPrivateKey(leafKey);
        X509Certificate2 leaf = X509CertificateLoader.LoadPkcs12(
            leafWithKey.Export(X509ContentType.Pkcs12), password: null,
            X509KeyStorageFlags.Exportable);
        leafPublic.Dispose();

        return (ca, leaf);
    }

    public static string WriteCaBundle(params X509Certificate2[] certificates)
    {
        string path = Path.Combine(Path.GetTempPath(), $"curlhttp-test-ca-{Guid.NewGuid():N}.pem");
        using var writer = new StreamWriter(path);
        foreach (X509Certificate2 certificate in certificates)
        {
            writer.WriteLine("-----BEGIN CERTIFICATE-----");
            writer.WriteLine(Convert.ToBase64String(
                certificate.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks));
            writer.WriteLine("-----END CERTIFICATE-----");
        }
        return path;
    }
}
