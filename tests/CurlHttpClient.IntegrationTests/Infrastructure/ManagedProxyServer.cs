using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CurlHttp.IntegrationTests.Infrastructure;

/// <summary>How the proxy misbehaves, for failure-mode tests.</summary>
public enum ProxyMode
{
    Normal,
    RequireBasicAuth,
    RejectAll,          // 403 on every request
    NeverRespond,       // accept, read, never answer
    Disconnect,         // accept then close immediately
    Malformed,          // reply with garbage
}

/// <summary>
/// Controlled HTTP forward proxy on a raw TcpListener: absolute-form
/// forwarding for plain HTTP and CONNECT tunneling for HTTPS. Records every
/// request line + auth header it sees so tests can assert exactly what went
/// through the proxy (including that nothing did).
/// </summary>
public sealed class ManagedProxyServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ProxyMode _mode;
    private readonly string? _expectedBasicAuth; // base64(user:pass)
    private readonly List<ProxyRequestRecord> _requests = [];
    private readonly object _sync = new();

    public sealed record ProxyRequestRecord(string RequestLine, string? ProxyAuthorization);

    public ManagedProxyServer(ProxyMode mode = ProxyMode.Normal,
        string? username = null, string? password = null)
    {
        _mode = mode;
        if (username is not null)
        {
            _expectedBasicAuth = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{username}:{password}"));
        }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }

    public IReadOnlyList<ProxyRequestRecord> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    public int ConnectCount => Requests.Count(r => r.RequestLine.StartsWith("CONNECT ", StringComparison.Ordinal));

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
            {
                NetworkStream stream = client.GetStream();
                if (_mode == ProxyMode.Disconnect)
                {
                    return; // close without reading
                }

                (string head, byte[] leftover) = await ReadHeadAsync(stream);
                if (head.Length == 0)
                {
                    return;
                }
                string[] lines = head.Split("\r\n");
                string requestLine = lines[0];
                string? auth = lines.Skip(1)
                    .FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                    ?.Split(':', 2)[1].Trim();
                lock (_sync)
                {
                    _requests.Add(new ProxyRequestRecord(requestLine, auth));
                }

                switch (_mode)
                {
                    case ProxyMode.RejectAll:
                        await WriteAsync(stream,
                            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    case ProxyMode.NeverRespond:
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        return;
                    case ProxyMode.Malformed:
                        await WriteAsync(stream, "BANANA/9.9 lol nope\r\n\r\n");
                        return;
                    case ProxyMode.RequireBasicAuth when !IsAuthorized(auth):
                        await WriteAsync(stream,
                            "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                            "Proxy-Authenticate: Basic realm=\"curlhttp-test\"\r\n" +
                            "Content-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                }

                if (requestLine.StartsWith("CONNECT ", StringComparison.Ordinal))
                {
                    await TunnelAsync(stream, requestLine);
                }
                else
                {
                    await ForwardAsync(stream, requestLine, head, leftover, lines);
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private bool IsAuthorized(string? proxyAuthorization)
        => _expectedBasicAuth is null ||
           proxyAuthorization == $"Basic {_expectedBasicAuth}";

    /// <summary>CONNECT host:port — establish the tunnel, answer 200, then
    /// pump bytes both ways (this is where the client's TLS runs end-to-end).</summary>
    private async Task TunnelAsync(NetworkStream client, string requestLine)
    {
        string hostPort = requestLine.Split(' ')[1];
        int colon = hostPort.LastIndexOf(':');
        string host = hostPort[..colon];
        int port = int.Parse(hostPort[(colon + 1)..]);

        using var upstream = new TcpClient();
        await upstream.ConnectAsync(host, port, _cts.Token);
        await WriteAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n");

        NetworkStream upstreamStream = upstream.GetStream();
        Task pumpUp = client.CopyToAsync(upstreamStream, _cts.Token);
        Task pumpDown = upstreamStream.CopyToAsync(client, _cts.Token);
        await Task.WhenAny(pumpUp, pumpDown);
    }

    /// <summary>Plain-HTTP forwarding of an absolute-form request.</summary>
    private async Task ForwardAsync(NetworkStream client, string requestLine,
        string head, byte[] leftover, string[] headerLines)
    {
        string[] parts = requestLine.Split(' ');
        var target = new Uri(parts[1]); // absolute-form URI
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(target.Host, target.Port, _cts.Token);
        NetworkStream upstreamStream = upstream.GetStream();

        // Rewrite to origin-form; strip hop-by-hop proxy headers.
        var rewritten = new StringBuilder()
            .Append(parts[0]).Append(' ').Append(target.PathAndQuery).Append(' ').Append(parts[2])
            .Append("\r\n");
        foreach (string line in headerLines.Skip(1))
        {
            if (line.Length == 0 ||
                line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            rewritten.Append(line).Append("\r\n");
        }
        rewritten.Append("\r\n");

        await WriteAsync(upstreamStream, rewritten.ToString());
        if (leftover.Length > 0)
        {
            await upstreamStream.WriteAsync(leftover, _cts.Token);
        }

        Task pumpUp = client.CopyToAsync(upstreamStream, _cts.Token);
        Task pumpDown = upstreamStream.CopyToAsync(client, _cts.Token);
        await Task.WhenAny(pumpUp, pumpDown);
    }

    private async Task<(string Head, byte[] Leftover)> ReadHeadAsync(NetworkStream stream)
    {
        var received = new List<byte>(16 * 1024);
        byte[] chunk = new byte[16 * 1024];
        int separator = -1;
        while (separator < 0)
        {
            int read = await stream.ReadAsync(chunk, _cts.Token);
            if (read == 0)
            {
                break;
            }
            received.AddRange(chunk.AsSpan(0, read));
            separator = FindHeaderEnd(received);
        }
        byte[] all = [.. received];
        if (separator < 0)
        {
            return (Encoding.ASCII.GetString(all), []);
        }
        return (Encoding.ASCII.GetString(all, 0, separator), all[(separator + 4)..]);
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private async Task WriteAsync(NetworkStream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token);
        await stream.FlushAsync(_cts.Token);
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
