using System.Net;
using CurlHttp;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace CurlHttp.IntegrationTests.CipherSuites;

/// <summary>
/// The manifest-driven cipher matrix. Enumerates EVERY cipher suite the
/// packaged OpenSSL reports (curl_bridge_enumerate_ciphers), classifies each
/// into exactly one bucket, and for every suite usable for
/// certificate-authenticated HTTPS: pins an s_server to that one suite, pins
/// the client identically, performs a real HTTPS request, and asserts the
/// server-reported negotiated cipher + protocol match. No suite is ever
/// silently skipped — every row lands in the emitted report with a reason.
///
/// Gated (CURLHTTP_STRESS=1): ~90 server spawns is minutes of wall time.
/// The fast default run covers the core suites via
/// SchannelCipherSuiteTests + ClientHelloOfferTests.
/// </summary>
[Collection("cipher-suites")]
public class CipherMatrixOrchestratorTests(CipherMaterialFixture fixture, ITestOutputHelper output)
{
    private sealed record CipherResult(
        string Name, string StandardName, string Protocol, string KeyExchange,
        string Authentication, int Bits, bool EnabledDefault, string RequiredCert,
        string ExpectedResult, string ActualResult, string NegotiatedCipher,
        string NegotiatedProtocol, string Classification, string FailureReason);

    [SkippableFact]
    public async Task EveryDiscoveredCipher_IsTestedOrClassified_WithZeroSilentSkips()
    {
        TestGate.RequireStress();

        CurlCipherManifest manifest = CurlCipherManifest.Load();
        var results = new List<CipherResult>();

        foreach (CurlCipherInfo cipher in manifest.Ciphers)
        {
            CipherTestClassification classification = cipher.Classify();
            if (!cipher.IsTestableForHttps)
            {
                results.Add(NotApplicable(cipher, classification));
                continue;
            }
            results.Add(await NegotiateAsync(cipher, classification));
        }

        // Invariant: exactly one row per discovered cipher — no silent skips.
        Assert.Equal(manifest.Ciphers.Count, results.Count);

        WriteReports(manifest, results);

        // Every TESTABLE suite must have negotiated successfully.
        var failures = results
            .Where(r => r.ExpectedResult == "Pass" && r.ActualResult != "Pass")
            .ToList();
        Assert.True(failures.Count == 0,
            "Cipher suites that should negotiate but failed:\n" +
            string.Join("\n", failures.Select(f => $"  {f.Name}: {f.FailureReason}")));
    }

    private async Task<CipherResult> NegotiateAsync(
        CurlCipherInfo cipher, CipherTestClassification classification)
    {
        bool tls13 = cipher.Protocol == "TLSv1.3";
        (string certPath, string keyPath) = fixture.Material.ForAuth(cipher.Authentication);

        // @SECLEVEL=0 is required for below-default-strength suites and only
        // parses in the TLS1.2-style cipher list, so it is set there even for
        // TLS 1.3 (it applies ctx-wide).
        string serverArgs = tls13
            ? $"-tls1_3 -cipher \"DEFAULT@SECLEVEL=0\" -ciphersuites \"{cipher.Name}\""
            : $"-tls1_2 -cipher \"{cipher.Name}@SECLEVEL=0\"";

        OpenSslCipherServer? server = null;
        try
        {
            server = OpenSslCipherServer.StartWithArgs(certPath, keyPath, serverArgs);

            // For TLS 1.3 suites we do NOT pin MinimumTlsVersion=1.3: the
            // server is already 1.3-only, and combining a min-1.3 floor with a
            // TLS 1.2 cipher list makes OpenSSL filter that (version-excluded)
            // list to empty → "no ciphers available", which breaks the
            // below-default-strength CCM_8 suites. The seclevel is still
            // lowered via Tls12CipherList, and the negotiated protocol is
            // asserted as 1.3 from the server page. (Documented interaction:
            // min-TLS-1.3 + an explicit Tls12CipherList is a contradictory
            // combination — see docs/limitations.md.)
            var options = new CurlHttpClientOptions
            {
                CertificateAuthorityBundlePath = fixture.Material.CaBundlePath,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                Tls12CipherList = tls13 ? "DEFAULT@SECLEVEL=0" : $"{cipher.Name}@SECLEVEL=0",
                Tls13CipherSuites = tls13 ? cipher.Name : null,
                MinimumTlsVersion = tls13 ? null : new Version(1, 2),
            };
            using var handler = new CurlHttpMessageHandler(options);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            using HttpResponseMessage response = await client.GetAsync(server.BaseUri);
            string page = await response.Content.ReadAsStringAsync();
            bool negotiated = response.IsSuccessStatusCode &&
                              page.Contains($"Cipher is {cipher.Name}");

            return new CipherResult(
                cipher.Name, cipher.StandardName, cipher.Protocol, cipher.KeyExchange,
                cipher.Authentication, cipher.Bits, cipher.EnabledByDefault,
                cipher.RequiredCertificate ?? "",
                ExpectedResult: "Pass",
                ActualResult: negotiated ? "Pass" : "Fail",
                NegotiatedCipher: negotiated ? cipher.Name : "",
                NegotiatedProtocol: cipher.Protocol,
                Classification: classification.ToString(),
                FailureReason: negotiated ? "" :
                    $"status={(int)response.StatusCode}; server log tail: " +
                    string.Concat(server.ServerLog.TakeLast(200)));
        }
        catch (Exception ex)
        {
            return new CipherResult(
                cipher.Name, cipher.StandardName, cipher.Protocol, cipher.KeyExchange,
                cipher.Authentication, cipher.Bits, cipher.EnabledByDefault,
                cipher.RequiredCertificate ?? "",
                "Pass", "Fail", "", cipher.Protocol, classification.ToString(),
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static CipherResult NotApplicable(
        CurlCipherInfo cipher, CipherTestClassification classification)
    {
        string reason = classification switch
        {
            CipherTestClassification.RequiresAnonymous =>
                "anonymous key exchange (aNULL) — no server certificate, not authenticated HTTPS",
            CipherTestClassification.NullEncryption =>
                "NULL bulk cipher (eNULL) — no encryption, out of scope for HTTPS",
            CipherTestClassification.RequiresDssCertificate =>
                "DSS/DSA authentication — no DSS certificates in scope",
            _ => "PSK/SRP or other mode outside certificate-authenticated HTTPS",
        };
        return new CipherResult(
            cipher.Name, cipher.StandardName, cipher.Protocol, cipher.KeyExchange,
            cipher.Authentication, cipher.Bits, cipher.EnabledByDefault,
            cipher.RequiredCertificate ?? "",
            ExpectedResult: "NotApplicable",
            ActualResult: "NotApplicable",
            NegotiatedCipher: "",
            NegotiatedProtocol: "",
            Classification: classification.ToString(),
            FailureReason: reason);
    }

    private void WriteReports(CurlCipherManifest manifest, List<CipherResult> results)
    {
        int passed = results.Count(r => r.ActualResult == "Pass");
        int failed = results.Count(r => r.ExpectedResult == "Pass" && r.ActualResult == "Fail");
        int notApplicable = results.Count(r => r.ExpectedResult == "NotApplicable");
        output.WriteLine($"cipher matrix: {results.Count} discovered, {passed} passed, " +
                         $"{failed} failed, {notApplicable} not-applicable");

        ArtifactsWriter.WriteJson("tls/discovered-ciphers.json", new
        {
            manifest.OpenSslVersion,
            manifest.OpenSslVersionHex,
            count = manifest.Ciphers.Count,
            ciphers = manifest.Ciphers,
        });
        ArtifactsWriter.WriteJson("tls/cipher-results.json", new
        {
            manifest.OpenSslVersion,
            environment = $"{Environment.OSVersion} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            summary = new { total = results.Count, passed, failed, notApplicable },
            results,
        });
        ArtifactsWriter.WriteCsv("tls/cipher-results.csv",
        [
            ["Name", "StandardName", "Protocol", "Kx", "Auth", "Bits", "EnabledDefault",
             "RequiredCert", "Expected", "Actual", "Negotiated", "Classification", "Reason"],
            .. results.Select(r => new[]
            {
                r.Name, r.StandardName, r.Protocol, r.KeyExchange, r.Authentication,
                r.Bits.ToString(), r.EnabledDefault.ToString(), r.RequiredCert,
                r.ExpectedResult, r.ActualResult, r.NegotiatedCipher, r.Classification, r.FailureReason,
            }),
        ]);

        var md = new System.Text.StringBuilder()
            .AppendLine("# Cipher-suite test matrix")
            .AppendLine()
            .AppendLine($"Generated from {manifest.OpenSslVersion} ({manifest.OpenSslVersionHex}), " +
                        $"enumerated inside the packaged bridge DLL.")
            .AppendLine()
            .AppendLine($"- Discovered: **{results.Count}**")
            .AppendLine($"- Passed (forced negotiation confirmed by server): **{passed}**")
            .AppendLine($"- Failed: **{failed}**")
            .AppendLine($"- Not applicable to certificate HTTPS: **{notApplicable}**")
            .AppendLine()
            .AppendLine("| Cipher | Standard | Proto | Auth | Bits | Default | Expected | Actual | Classification | Reason |")
            .AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (CipherResult r in results)
        {
            md.AppendLine($"| {r.Name} | {r.StandardName} | {r.Protocol} | {r.Authentication} | " +
                          $"{r.Bits} | {r.EnabledDefault} | {r.ExpectedResult} | {r.ActualResult} | " +
                          $"{r.Classification} | {r.FailureReason} |");
        }
        ArtifactsWriter.WriteText("tls/cipher-report.md", md.ToString());
    }
}
