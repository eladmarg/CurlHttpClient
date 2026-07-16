using Xunit;

namespace CurlHttp.IntegrationTests.CipherSuites;

// The cipher matrix runs unconditionally: these tests target a modern OS
// (and an openssl CLI, e.g. from Git for Windows). A missing prerequisite is
// a broken test environment and fails loudly instead of skipping.
public sealed class OpenSslTheoryAttribute : TheoryAttribute;

public sealed class OpenSslFactAttribute : FactAttribute;

/// <summary>
/// The required cipher-suite support matrix. Every "Yes" suite is validated
/// by REAL negotiation: an `openssl s_server` pinned to exactly that one
/// suite (so nothing else can be picked), our handler connecting with its
/// DEFAULT TLS configuration, and the negotiated cipher read back from the
/// server's status page. ECDHE_ECDSA suites get an ECDSA P-256 server
/// certificate; everything else an RSA-2048 one.
/// </summary>
[Collection("cipher-suites")] // serialize: each case owns a server process
public class CipherSuiteNegotiationTests(CipherMaterialFixture fixture)
{
    /// <summary>IANA name, OpenSSL name, TLS 1.3 flag, needs-ECDSA-cert flag.</summary>
    public static TheoryData<string, string, bool, bool> SupportedSuites => new()
    {
        // -- TLS 1.3 --
        { "TLS_AES_256_GCM_SHA384", "TLS_AES_256_GCM_SHA384", true, false },
        { "TLS_AES_128_GCM_SHA256", "TLS_AES_128_GCM_SHA256", true, false },
        // -- TLS 1.2, ECDHE + ECDSA --
        { "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", "ECDHE-ECDSA-AES256-GCM-SHA384", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", "ECDHE-ECDSA-AES128-GCM-SHA256", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384", "ECDHE-ECDSA-AES256-SHA384", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256", "ECDHE-ECDSA-AES128-SHA256", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", "ECDHE-ECDSA-AES256-SHA", false, true },
        { "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", "ECDHE-ECDSA-AES128-SHA", false, true },
        // -- TLS 1.2, ECDHE + RSA --
        { "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", "ECDHE-RSA-AES256-GCM-SHA384", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", "ECDHE-RSA-AES128-GCM-SHA256", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384", "ECDHE-RSA-AES256-SHA384", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256", "ECDHE-RSA-AES128-SHA256", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA", "ECDHE-RSA-AES256-SHA", false, false },
        { "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", "ECDHE-RSA-AES128-SHA", false, false },
        // -- TLS 1.2, DHE + RSA --
        { "TLS_DHE_RSA_WITH_AES_256_GCM_SHA384", "DHE-RSA-AES256-GCM-SHA384", false, false },
        { "TLS_DHE_RSA_WITH_AES_128_GCM_SHA256", "DHE-RSA-AES128-GCM-SHA256", false, false },
        // -- TLS 1.2, plain RSA key exchange --
        { "TLS_RSA_WITH_AES_256_GCM_SHA384", "AES256-GCM-SHA384", false, false },
        { "TLS_RSA_WITH_AES_128_GCM_SHA256", "AES128-GCM-SHA256", false, false },
        { "TLS_RSA_WITH_AES_256_CBC_SHA256", "AES256-SHA256", false, false },
        { "TLS_RSA_WITH_AES_128_CBC_SHA256", "AES128-SHA256", false, false },
        { "TLS_RSA_WITH_AES_256_CBC_SHA", "AES256-SHA", false, false },
        { "TLS_RSA_WITH_AES_128_CBC_SHA", "AES128-SHA", false, false },
    };

    [OpenSslTheory]
    [MemberData(nameof(SupportedSuites))]
    public async Task Handler_Negotiates_WhenServerRequiresExactlyThisSuite(
        string ianaName, string opensslName, bool tls13, bool needsEcdsaCert)
    {
        using OpenSslCipherServer server = OpenSslCipherServer.Start(
            needsEcdsaCert ? fixture.Material.EcdsaCertPath : fixture.Material.RsaCertPath,
            needsEcdsaCert ? fixture.Material.EcdsaKeyPath : fixture.Material.RsaKeyPath,
            tls13,
            opensslName);

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        string statusPage = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode,
            $"{ianaName}: request failed with {(int)response.StatusCode}.\nserver log:\n{server.ServerLog}");
        // The s_server -www page prints the ACTUAL negotiated parameters,
        // e.g. "New, TLSv1.2, Cipher is ECDHE-RSA-AES256-GCM-SHA384".
        Assert.Contains($"Cipher is {opensslName}", statusPage);
        Assert.Contains(tls13 ? "TLSv1.3" : "TLSv1.2", statusPage);
    }

    [OpenSslFact]
    public async Task Tls12CipherListOption_PinsTheClientSide()
    {
        // Server allows ANY TLS 1.2 suite; the client is restricted through
        // CurlHttpClientOptions — proving the option reaches OpenSSL.
        using OpenSslCipherServer server = OpenSslCipherServer.Start(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            tls13: false, cipher: "ALL");

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            Tls12CipherList = "ECDHE-RSA-AES128-GCM-SHA256",
        });
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
        string statusPage = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cipher is ECDHE-RSA-AES128-GCM-SHA256", statusPage);
    }

    [OpenSslFact]
    public async Task StressMix_ParallelClientsAgainstDifferentlyPinnedServers()
    {
        // Six servers, each demanding a different suite across all key-exchange
        // families, hit by 5 parallel requests each through one shared handler —
        // exercises cipher negotiation under concurrency on warm and cold
        // connections alike.
        (string OpensslName, bool Tls13, bool Ecdsa)[] mix =
        [
            ("TLS_AES_256_GCM_SHA384", true, false),
            ("ECDHE-ECDSA-AES256-GCM-SHA384", false, true),
            ("ECDHE-RSA-AES128-GCM-SHA256", false, false),
            ("DHE-RSA-AES256-GCM-SHA384", false, false),
            ("AES128-SHA", false, false),
            ("ECDHE-ECDSA-AES128-SHA", false, true),
        ];

        var servers = mix.Select(m => OpenSslCipherServer.Start(
            m.Ecdsa ? fixture.Material.EcdsaCertPath : fixture.Material.RsaCertPath,
            m.Ecdsa ? fixture.Material.EcdsaKeyPath : fixture.Material.RsaKeyPath,
            m.Tls13, m.OpensslName)).ToList();
        try
        {
            using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

            IEnumerable<Task> requests = servers.SelectMany((server, i) =>
                Enumerable.Range(0, 5).Select(async _ =>
                {
                    using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
                    string page = await response.Content.ReadAsStringAsync();
                    Assert.Contains($"Cipher is {mix[i].OpensslName}", page);
                }));
            await Task.WhenAll(requests);
        }
        finally
        {
            foreach (OpenSslCipherServer server in servers)
            {
                server.Dispose();
            }
        }
    }

    [OpenSslFact]
    public async Task TripleDes_CannotEvenBeConfigured_OnTheClient()
    {
        // The bundled OpenSSL does not compile 3DES at all: a client pinned to
        // TLS_RSA_WITH_3DES_EDE_CBC_SHA has an empty cipher list and must fail
        // the handshake against ANY server.
        using OpenSslCipherServer server = OpenSslCipherServer.Start(
            fixture.Material.RsaCertPath, fixture.Material.RsaKeyPath,
            tls13: false, cipher: "ALL");

        using var handler = new CurlHttpMessageHandler(new CurlHttpClientOptions
        {
            CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
            Tls12CipherList = "DES-CBC3-SHA",
        });
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(server.BaseUri));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
    }
}

/// <summary>
/// Wire-level ground truth: parse the actual ClientHello the handler sends
/// and check the offered cipher-suite code points (IANA numbers), including
/// the REQUIRED ABSENCE of TLS_RSA_WITH_3DES_EDE_CBC_SHA (0x000A).
/// </summary>
[Collection("cipher-suites")]
public class ClientHelloOfferTests(CipherMaterialFixture fixture)
{
    private static readonly (string Name, ushort Id)[] RequiredOffers =
    [
        ("TLS_AES_256_GCM_SHA384", 0x1302),
        ("TLS_AES_128_GCM_SHA256", 0x1301),
        ("TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", 0xC02C),
        ("TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", 0xC02B),
        ("TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", 0xC030),
        ("TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", 0xC02F),
        ("TLS_DHE_RSA_WITH_AES_256_GCM_SHA384", 0x009F),
        ("TLS_DHE_RSA_WITH_AES_128_GCM_SHA256", 0x009E),
        ("TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384", 0xC024),
        ("TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256", 0xC023),
        ("TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384", 0xC028),
        ("TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256", 0xC027),
        ("TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", 0xC00A),
        ("TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", 0xC009),
        ("TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA", 0xC014),
        ("TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", 0xC013),
        ("TLS_RSA_WITH_AES_256_GCM_SHA384", 0x009D),
        ("TLS_RSA_WITH_AES_128_GCM_SHA256", 0x009C),
        ("TLS_RSA_WITH_AES_256_CBC_SHA256", 0x003D),
        ("TLS_RSA_WITH_AES_128_CBC_SHA256", 0x003C),
        ("TLS_RSA_WITH_AES_256_CBC_SHA", 0x0035),
        ("TLS_RSA_WITH_AES_128_CBC_SHA", 0x002F),
    ];

    private const ushort TripleDesSuite = 0x000A; // TLS_RSA_WITH_3DES_EDE_CBC_SHA

    [Fact]
    public async Task DefaultClientHello_OffersEveryRequiredSuite_AndNever3Des()
    {
        IReadOnlyList<ushort> offered = await ClientHelloCapture.CaptureOfferedSuitesAsync(
            new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        var missing = RequiredOffers.Where(s => !offered.Contains(s.Id)).ToList();
        Assert.True(missing.Count == 0,
            "ClientHello is missing required suites: " +
            string.Join(", ", missing.Select(m => m.Name)) +
            $". Offered: {string.Join(", ", offered.Select(o => $"0x{o:X4}"))}");

        Assert.DoesNotContain(TripleDesSuite, offered);
    }

    [Fact]
    public async Task MinimumTls13_RemovesTls12SuitesFromTheOffer()
    {
        IReadOnlyList<ushort> offered = await ClientHelloCapture.CaptureOfferedSuitesAsync(
            new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                MinimumTlsVersion = new Version(1, 3),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        Assert.Contains((ushort)0x1302, offered); // TLS_AES_256_GCM_SHA384
        Assert.Contains((ushort)0x1301, offered); // TLS_AES_128_GCM_SHA256
        Assert.DoesNotContain((ushort)0xC02F, offered); // no TLS 1.2 ECDHE-RSA-GCM
        Assert.DoesNotContain((ushort)0x002F, offered); // no TLS 1.2 RSA-CBC
        Assert.DoesNotContain(TripleDesSuite, offered);
    }
}

public sealed class CipherMaterialFixture : IDisposable
{
    public CipherTestMaterial Material { get; } = new();
    public void Dispose() => Material.Dispose();
}

[CollectionDefinition("cipher-suites")]
public sealed class CipherSuiteCollection : ICollectionFixture<CipherMaterialFixture>;
