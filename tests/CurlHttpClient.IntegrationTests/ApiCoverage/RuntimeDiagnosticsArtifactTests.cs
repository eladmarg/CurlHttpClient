using System.Runtime.InteropServices;
using CurlHttp.IntegrationTests.Infrastructure;
using Xunit;

namespace CurlHttp.IntegrationTests.ApiCoverage;

/// <summary>Emits the runtime-diagnostics and OpenSSL-build-info artifacts
/// (also a live proof that the packaged stack is OpenSSL, not Schannel).</summary>
[Collection("integration")]
public class RuntimeDiagnosticsArtifactTests(ServerFixture fixture)
{
    [Fact]
    public void EmitRuntimeDiagnostics_AndProveOpenSslBackend()
    {
        CurlHttpClientDiagnostics diag = fixture.Handler.GetDiagnostics();
        Assert.True(diag.TlsBackendIsOpenSsl);

        ArtifactsWriter.WriteJson("compatibility/runtime-diagnostics.json", new
        {
            diag.BridgeVersion,
            diag.CurlVersion,
            diag.SslVersion,
            diag.TlsBackendIsOpenSsl,
            diag.SupportsHttp2,
            diag.SupportsBrotli,
            diag.SupportsAsyncDns,
            diag.Features,
            diag.Protocols,
            diag.TrustSource,
            diag.NativeLibraryPath,
            diag.ProcessArchitecture,
            os = RuntimeInformation.OSDescription,
            framework = RuntimeInformation.FrameworkDescription,
            capturedUtc = DateTime.UtcNow,
        });
    }

    [Fact]
    public void EmitOpenSslBuildInfo_FromExactPackagedBuild()
    {
        CurlCipherManifest manifest = CurlCipherManifest.Load();
        ArtifactsWriter.WriteJson("tls/openssl-build-info.json", new
        {
            manifest.OpenSslVersion,
            manifest.OpenSslVersionHex,
            cipherCount = manifest.Ciphers.Count,
            tls13Suites = manifest.Ciphers.Count(c => c.Protocol == "TLSv1.3"),
            tls12Suites = manifest.Ciphers.Count(c => c.Protocol == "TLSv1.2"),
            certAuthenticatedSuites = manifest.Ciphers.Count(c => c.IsTestableForHttps),
            capturedUtc = DateTime.UtcNow,
        });
    }
}
