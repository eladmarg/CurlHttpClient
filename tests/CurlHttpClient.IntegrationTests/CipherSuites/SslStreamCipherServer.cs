using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CurlHttp.IntegrationTests.CipherSuites;

/// <summary>
/// Minimal HTTPS server on the OPERATING SYSTEM's TLS stack (SslStream →
/// Schannel on Windows). The OS negotiates from its own default cipher
/// configuration; each response body reports the cipher Schannel actually
/// selected (<see cref="SslStream.NegotiatedCipherSuite"/>), so tests can pin
/// the CLIENT to one suite and assert the OS agreed to exactly it.
/// </summary>
internal sealed class SslStreamCipherServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly SslProtocols _protocols;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public Uri BaseUri { get; }

    public SslStreamCipherServer(X509Certificate2 certificate, SslProtocols protocols)
    {
        _certificate = certificate;
        _protocols = protocols;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var ssl = new SslStream(client.GetStream()))
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = _protocols,
                    ClientCertificateRequired = false,
                }, _cts.Token);

                // Drain the request head; the content is irrelevant.
                byte[] buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await ssl.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        return;
                    }
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                string body = ssl.NegotiatedCipherSuite.ToString();
                byte[] payload = Encoding.ASCII.GetBytes(body);
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/plain\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await ssl.WriteAsync(response, _cts.Token);
                await ssl.WriteAsync(payload, _cts.Token);
                await ssl.FlushAsync(_cts.Token);
            }
        }
        catch (Exception)
        {
            // Handshake failures are a legitimate test outcome (the client
            // observes them); nothing to do server-side.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }
}
