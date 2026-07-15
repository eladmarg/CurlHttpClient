using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CurlHttp.IntegrationTests.CipherSuites;
using Xunit;

namespace CurlHttp.IntegrationTests.Tls;

/// <summary>
/// Certificate-validation matrix against an SslStream server presenting
/// deterministically-generated certificates. Peer + hostname verification
/// stay enabled throughout — the negative cases prove rejection, never a
/// disabled-verification bypass.
/// </summary>
[Collection("cipher-suites")]
public class CertificateValidationMatrixTests
{
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) MakeChain(
        Action<CertificateRequest>? customizeLeaf = null,
        DateTimeOffset? leafNotBefore = null, DateTimeOffset? leafNotAfter = null,
        string leafCn = "localhost", string[]? sans = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // The CA validity must span any leaf window the tests request
        // (including not-yet-valid and already-expired leaves).
        using RSA caKey = RSA.Create(2048);
        var caReq = new CertificateRequest($"CN=Cert Matrix CA {Guid.NewGuid():N}",
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign, true));
        X509Certificate2 ca = caReq.CreateSelfSigned(now.AddDays(-60), now.AddDays(60));

        using RSA leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest($"CN={leafCn}", leafKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (string s in sans ?? ["localhost"])
        {
            if (IPAddress.TryParse(s, out IPAddress? ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(s);
            }
        }
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        customizeLeaf?.Invoke(leafReq);

        X509Certificate2 pub = leafReq.Create(ca,
            leafNotBefore ?? now.AddDays(-1), leafNotAfter ?? now.AddDays(30),
            RandomNumberGenerator.GetBytes(12));
        using X509Certificate2 withKey = pub.CopyWithPrivateKey(leafKey);
        X509Certificate2 leaf = X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        pub.Dispose();
        return (ca, leaf);
    }

    private static string WriteCaBundle(params X509Certificate2[] certs)
    {
        string path = Path.Combine(Path.GetTempPath(), $"certmatrix-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, string.Join("\n", certs.Select(c => c.ExportCertificatePem())));
        return path;
    }

    private static async Task<HttpResponseMessage> RequestAsync(
        X509Certificate2 serverCert, string caBundlePath, string host = "localhost")
    {
        using var server = new SslStreamCertServer(serverCert);
        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = caBundlePath,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });
        using var client = new HttpClient(handler);
        return await client.GetAsync(new Uri($"https://{host}:{server.Port}/"));
    }

    [Fact]
    public async Task TrustedChain_CorrectHostname_Succeeds()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain();
        string bundle = WriteCaBundle(ca);
        try
        {
            using HttpResponseMessage response = await RequestAsync(leaf, bundle);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task UntrustedRoot_IsRejected()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain();
        (X509Certificate2 otherCa, X509Certificate2 _) = MakeChain();
        string bundle = WriteCaBundle(otherCa); // does not contain leaf's issuer
        try
        {
            await AssertSecureFailure(leaf, bundle);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            otherCa.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task HostnameMismatch_IsRejected()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain(sans: ["other.example"]);
        string bundle = WriteCaBundle(ca);
        try
        {
            await AssertSecureFailure(leaf, bundle, host: "127.0.0.1");
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task ExpiredCertificate_IsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain(
            leafNotBefore: now.AddDays(-10), leafNotAfter: now.AddDays(-1));
        string bundle = WriteCaBundle(ca);
        try
        {
            await AssertSecureFailure(leaf, bundle);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task NotYetValidCertificate_IsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain(
            leafNotBefore: now.AddDays(2), leafNotAfter: now.AddDays(10));
        string bundle = WriteCaBundle(ca);
        try
        {
            await AssertSecureFailure(leaf, bundle);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task WrongExtendedKeyUsage_ClientAuthOnly_IsRejected()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain(customizeLeaf: req =>
        {
            // Replace serverAuth EKU with clientAuth only.
            for (int i = req.CertificateExtensions.Count - 1; i >= 0; i--)
            {
                if (req.CertificateExtensions[i] is X509EnhancedKeyUsageExtension)
                {
                    req.CertificateExtensions.RemoveAt(i);
                }
            }
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth
        });
        string bundle = WriteCaBundle(ca);
        try
        {
            // OpenSSL with default purpose checking rejects a server cert
            // lacking serverAuth EKU.
            await AssertSecureFailure(leaf, bundle);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public async Task IpAddressSan_Matches_WhenConnectingByIp()
    {
        (X509Certificate2 ca, X509Certificate2 leaf) = MakeChain(
            leafCn: "127.0.0.1", sans: ["127.0.0.1"]);
        string bundle = WriteCaBundle(ca);
        try
        {
            using HttpResponseMessage response = await RequestAsync(leaf, bundle, host: "127.0.0.1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            ca.Dispose();
            leaf.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public void EmptyCaBundle_IsRefusedAtConstruction()
    {
        // An empty PEM bundle provides no trust source; since verification can
        // never be disabled, the handler refuses to start rather than trust
        // everything. (A populated-but-non-matching bundle is the runtime
        // rejection case, covered by UntrustedRoot_IsRejected.)
        string empty = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.pem");
        File.WriteAllText(empty, "");
        try
        {
            InvalidOperationException ex = Assert.ThrowsAny<InvalidOperationException>(() =>
                new CurlHttpMessageHandler(new CurlHttpClientOptions
                {
                    CertificateAuthorityBundlePath = empty,
                }));
            Assert.Contains("trust source", ex.Message);
        }
        finally
        {
            File.Delete(empty);
        }
    }

    [Fact]
    public async Task MultipleTrustedRoots_InBundle_OneMatches()
    {
        (X509Certificate2 ca1, X509Certificate2 _) = MakeChain();
        (X509Certificate2 ca2, X509Certificate2 leaf2) = MakeChain();
        (X509Certificate2 ca3, X509Certificate2 _) = MakeChain();
        string bundle = WriteCaBundle(ca1, ca2, ca3); // leaf2's issuer is in the middle
        try
        {
            using HttpResponseMessage response = await RequestAsync(leaf2, bundle);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            ca1.Dispose();
            ca2.Dispose();
            ca3.Dispose();
            leaf2.Dispose();
            File.Delete(bundle);
        }
    }

    [Fact]
    public void MissingCaBundleFile_RejectsAtConstruction()
    {
        Assert.Throws<FileNotFoundException>(() => new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = @"C:\no\such\bundle.pem",
        }));
    }

    private static async Task AssertSecureFailure(
        X509Certificate2 serverCert, string caBundlePath, string host = "localhost")
    {
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => RequestAsync(serverCert, caBundlePath, host));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }

    /// <summary>Minimal always-TLS1.2/1.3 SslStream server presenting one cert.</summary>
    private sealed class SslStreamCertServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port { get; }

        public SslStreamCertServer(X509Certificate2 certificate)
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            await using (var ssl = new SslStream(client.GetStream()))
                            {
                                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = certificate,
                                    EnabledSslProtocols =
                                        System.Security.Authentication.SslProtocols.Tls12 |
                                        System.Security.Authentication.SslProtocols.Tls13,
                                }, _cts.Token);
                                byte[] buffer = new byte[4096];
                                var head = new System.Text.StringBuilder();
                                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    int read = await ssl.ReadAsync(buffer, _cts.Token);
                                    if (read == 0)
                                    {
                                        return;
                                    }
                                    head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                                }
                                await ssl.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"),
                                    _cts.Token);
                            }
                        }
                        catch
                        {
                            // handshake rejection is a valid outcome
                        }
                    });
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            _cts.Dispose();
        }
    }
}
