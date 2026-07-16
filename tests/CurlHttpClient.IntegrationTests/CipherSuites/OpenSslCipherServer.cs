using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CurlHttp.IntegrationTests.CipherSuites;

/// <summary>
/// Wraps `openssl s_server -www` pinned to exactly ONE cipher suite, so each
/// test proves the handler can negotiate that specific suite (the server
/// refuses everything else). The negotiated cipher is read back from the
/// s_server status page, so the assertion is on what actually happened on
/// the wire, not on configuration.
/// </summary>
internal sealed class OpenSslCipherServer : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _serverLog = new();

    public int Port { get; }
    public Uri BaseUri { get; }

    private OpenSslCipherServer(Process process, int port)
    {
        _process = process;
        Port = port;
        BaseUri = new Uri($"https://127.0.0.1:{port}/");
    }

    /// <summary>Resolved path of openssl.exe, or null when unavailable
    /// (used by the conditional test attributes to skip).</summary>
    public static string? OpenSslExecutable { get; } = Locate();

    private static string? Locate()
    {
        string? configured = Environment.GetEnvironmentVariable("CURLHTTP_OPENSSL_EXE");
        if (configured is not null && File.Exists(configured))
        {
            return configured;
        }
        // Git for Windows ships a full OpenSSL CLI.
        string gitOpenSsl = @"C:\Program Files\Git\mingw64\bin\openssl.exe";
        if (File.Exists(gitOpenSsl))
        {
            return gitOpenSsl;
        }
        return null;
    }

    public static OpenSslCipherServer Start(
        string certPemPath, string keyPemPath, bool tls13, string cipher)
    {
        // The free-port probe releases the port before s_server binds it, so
        // a parallel test can steal it in between; s_server then exits
        // immediately. Retry with a fresh port instead of failing the test.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return StartOnce(certPemPath, keyPemPath, tls13, cipher);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
            }
            catch (TimeoutException) when (attempt < 3)
            {
            }
        }
    }

    private static OpenSslCipherServer StartOnce(
        string certPemPath, string keyPemPath, bool tls13, string cipher)
    {
        // Called from Start(), which already owns the port-steal retry loop —
        // delegate to the single-attempt launcher so retries don't nest.
        string protocolArgs = tls13
            ? $"-tls1_3 -ciphersuites \"{cipher}\""
            : $"-tls1_2 -cipher \"{cipher}\"";
        return StartWithArgsOnce(certPemPath, keyPemPath, protocolArgs);
    }

    /// <summary>Full-control launcher for the TLS matrices: caller supplies
    /// the exact protocol/cipher/groups/client-cert arguments.</summary>
    public static OpenSslCipherServer StartWithArgs(
        string certPemPath, string keyPemPath, string extraArgs)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return StartWithArgsOnce(certPemPath, keyPemPath, extraArgs);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
            }
            catch (TimeoutException) when (attempt < 3)
            {
            }
        }
    }

    private static OpenSslCipherServer StartWithArgsOnce(
        string certPemPath, string keyPemPath, string extraArgs)
    {
        string exe = OpenSslExecutable
            ?? throw new InvalidOperationException(
                "openssl.exe not found; set CURLHTTP_OPENSSL_EXE or install Git for Windows.");

        int port = GetFreePort();
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"s_server -accept {port} -www -cert \"{certPemPath}\" " +
                        $"-key \"{keyPemPath}\" {extraArgs}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start openssl s_server");
        var server = new OpenSslCipherServer(process, port);
        process.OutputDataReceived += (_, e) => server.Append(e.Data);
        process.ErrorDataReceived += (_, e) => server.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        server.WaitUntilListening();
        return server;
    }

    private void Append(string? line)
    {
        if (line is not null)
        {
            lock (_serverLog)
            {
                _serverLog.AppendLine(line);
            }
        }
    }

    public string ServerLog
    {
        get
        {
            lock (_serverLog)
            {
                return _serverLog.ToString();
            }
        }
    }

    private void WaitUntilListening()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"openssl s_server exited immediately (bad cipher pin?):\n{ServerLog}");
            }
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, Port);
                return; // listening (the aborted probe just fails one handshake)
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new TimeoutException($"openssl s_server did not start listening:\n{ServerLog}");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}

/// <summary>PEM material for the cipher matrix: one throwaway CA signing an
/// RSA leaf (RSA / ECDHE_RSA / DHE_RSA suites) and an ECDSA P-256 leaf
/// (ECDHE_ECDSA suites), both valid for 127.0.0.1.</summary>
public sealed class CipherTestMaterial : IDisposable
{
    private readonly string _directory;

    public string CaBundlePath { get; }
    public string RsaCertPath { get; }
    public string RsaKeyPath { get; }
    public string EcdsaCertPath { get; }
    public string EcdsaKeyPath { get; }
    public string EcdsaP384CertPath { get; }
    public string EcdsaP384KeyPath { get; }

    /// <summary>Server PEM cert/key paths for the certificate type a cipher's
    /// authentication requires ("RSA", "ECDSA", "any").</summary>
    public (string Cert, string Key) ForAuth(string auth) => auth switch
    {
        "ECDSA" => (EcdsaCertPath, EcdsaKeyPath),
        _ => (RsaCertPath, RsaKeyPath), // RSA and TLS1.3 "any" use the RSA leaf
    };

    /// <summary>Server certificates with persisted private keys, for the
    /// SslStream (Schannel) server layer.</summary>
    public X509Certificate2 RsaCertificate { get; }
    public X509Certificate2 EcdsaCertificate { get; }

    public CipherTestMaterial()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"curlhttp-ciphers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        // Single clock read; leaves strictly inside the CA validity (see the
        // identical comment in TestCertificates.CreateChain).
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset caNotAfter = notBefore.AddDays(8);
        DateTimeOffset leafNotAfter = notBefore.AddDays(7);

        using RSA caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            $"CN=CurlHttpClient Cipher Test CA {Guid.NewGuid():N}",
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(notBefore, caNotAfter);

        using RSA rsaLeafKey = RSA.Create(2048);
        var rsaRequest = new CertificateRequest(
            "CN=cipher-test-rsa", rsaLeafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddLeafExtensions(rsaRequest);
        using X509Certificate2 rsaLeaf = rsaRequest.Create(
            ca, notBefore, leafNotAfter, RandomNumberGenerator.GetBytes(12));

        // The EC requests have no RSA padding to infer, so signing with the
        // RSA CA requires the explicit signature-generator overload.
        var caSignatureGenerator = X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1);

        using ECDsa ecdsaLeafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecdsaRequest = new CertificateRequest(
            "CN=cipher-test-ecdsa", ecdsaLeafKey, HashAlgorithmName.SHA256);
        AddLeafExtensions(ecdsaRequest);
        using X509Certificate2 ecdsaLeaf = ecdsaRequest.Create(
            ca.SubjectName, caSignatureGenerator,
            notBefore, leafNotAfter,
            RandomNumberGenerator.GetBytes(12));

        using ECDsa ecdsaP384Key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var ecdsaP384Request = new CertificateRequest(
            "CN=cipher-test-ecdsa-p384", ecdsaP384Key, HashAlgorithmName.SHA384);
        AddLeafExtensions(ecdsaP384Request);
        using X509Certificate2 ecdsaP384Leaf = ecdsaP384Request.Create(
            ca.SubjectName, caSignatureGenerator,
            notBefore, leafNotAfter,
            RandomNumberGenerator.GetBytes(12));

        CaBundlePath = Write("ca.pem", ca.ExportCertificatePem());
        RsaCertPath = Write("rsa-leaf.pem", rsaLeaf.ExportCertificatePem());
        RsaKeyPath = Write("rsa-leaf.key.pem", rsaLeafKey.ExportPkcs8PrivateKeyPem());
        EcdsaCertPath = Write("ecdsa-leaf.pem", ecdsaLeaf.ExportCertificatePem());
        EcdsaKeyPath = Write("ecdsa-leaf.key.pem", ecdsaLeafKey.ExportPkcs8PrivateKeyPem());
        EcdsaP384CertPath = Write("ecdsa-p384-leaf.pem", ecdsaP384Leaf.ExportCertificatePem());
        EcdsaP384KeyPath = Write("ecdsa-p384-leaf.key.pem", ecdsaP384Key.ExportPkcs8PrivateKeyPem());

        // PKCS#12 round-trip persists the private keys so Schannel (SslStream
        // server side) accepts them.
        using X509Certificate2 rsaWithKey = rsaLeaf.CopyWithPrivateKey(rsaLeafKey);
        RsaCertificate = X509CertificateLoader.LoadPkcs12(
            rsaWithKey.Export(X509ContentType.Pkcs12), password: null,
            X509KeyStorageFlags.Exportable);
        using X509Certificate2 ecdsaWithKey = ecdsaLeaf.CopyWithPrivateKey(ecdsaLeafKey);
        EcdsaCertificate = X509CertificateLoader.LoadPkcs12(
            ecdsaWithKey.Export(X509ContentType.Pkcs12), password: null,
            X509KeyStorageFlags.Exportable);
    }

    private static void AddLeafExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
    }

    private string Write(string name, string pem)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, pem);
        return path;
    }

    public void Dispose()
    {
        RsaCertificate.Dispose();
        EcdsaCertificate.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
