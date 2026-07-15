using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CurlHttp.IntegrationTests;

/// <summary>One-shot raw TCP server for responses Kestrel refuses to produce:
/// malformed status lines, truncated bodies, abrupt connection closes.</summary>
internal sealed class RawTcpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUri { get; }

    public RawTcpServer(byte[] responseBytes, bool closeAbruptly = true)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                NetworkStream stream = client.GetStream();

                // Read until the request head is complete.
                byte[] buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                await stream.WriteAsync(responseBytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                if (closeAbruptly)
                {
                    client.Client.Close();
                }
            }
        });
    }

    public static RawTcpServer TruncatedBody() => new(Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 1000\r\n\r\nonly-a-fragment"));

    public static RawTcpServer MalformedStatusLine() => new(Encoding.ASCII.GetBytes(
        "TOTALLY NOT HTTP\r\n\r\nnope"));

    public static RawTcpServer CloseBeforeResponse() => new([]);

    /// <summary>Well-formed chunked response with a trailer field —
    /// deterministic replacement for Kestrel, which refuses HTTP/1.1
    /// trailers unless the client advertises "TE: trailers".</summary>
    public static RawTcpServer ChunkedWithTrailers() => new(Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain\r\n" +
        "Trailer: X-Checksum\r\n" +
        "Transfer-Encoding: chunked\r\n" +
        "\r\n" +
        "c\r\nchunked body\r\n" +
        "0\r\n" +
        "X-Checksum: abc123\r\n" +
        "\r\n"), closeAbruptly: false);

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
