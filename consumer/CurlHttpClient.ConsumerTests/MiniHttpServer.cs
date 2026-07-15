using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CurlHttp.ConsumerTests;

/// <summary>
/// Dependency-free local HTTP server (raw TcpListener) so the consumer
/// solution needs nothing beyond the package under test. One connection per
/// request (Connection: close) keeps the protocol handling trivial.
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public Uri BaseUri { get; }

    public MiniHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
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
            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                (string method, string path, Dictionary<string, string> headers, byte[] leftover) =
                    await ReadRequestHeadAsync(stream);

                byte[] body = [];
                if (headers.TryGetValue("content-length", out string? lengthValue) &&
                    int.TryParse(lengthValue, out int contentLength) && contentLength > 0)
                {
                    // Body bytes that arrived in the same reads as the head
                    // come first; the rest is read off the socket.
                    body = new byte[contentLength];
                    int offset = Math.Min(leftover.Length, contentLength);
                    leftover.AsSpan(0, offset).CopyTo(body);
                    while (offset < contentLength)
                    {
                        int read = await stream.ReadAsync(
                            body.AsMemory(offset, contentLength - offset), _cts.Token);
                        if (read == 0)
                        {
                            break;
                        }
                        offset += read;
                    }
                }

                await RouteAsync(stream, method, path, headers, body);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            // Shutdown races are expected.
        }
        catch (IOException)
        {
            // Client aborted (cancellation tests do this on purpose).
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Leftover)>
        ReadRequestHeadAsync(NetworkStream stream)
    {
        // Byte-accurate head parsing: anything received past the CRLFCRLF
        // separator is BODY and must be handed back, not discarded.
        var received = new List<byte>(16 * 1024);
        var chunk = new byte[16 * 1024];
        int separator = -1;
        while (separator < 0)
        {
            int read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }
            received.AddRange(chunk.AsSpan(0, read));
            separator = FindHeaderEnd(received);
        }

        byte[] all = [.. received];
        int headLength = separator >= 0 ? separator : all.Length;
        byte[] leftover = separator >= 0 ? all[(separator + 4)..] : [];

        string[] lines = Encoding.ASCII.GetString(all, 0, headLength).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }
        return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", headers, leftover);
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

    private async Task RouteAsync(
        NetworkStream stream, string method, string path,
        Dictionary<string, string> requestHeaders, byte[] body)
    {
        string pathOnly = path.Split('?')[0];
        switch (method, pathOnly)
        {
            case ("GET", "/hello"):
                await WriteSimpleAsync(stream, 200, "OK", "text/plain", "hello consumer",
                    "X-Server: MiniHttpServer");
                break;

            case ("POST", "/echo-length"):
                await WriteSimpleAsync(stream, 200, "OK", "text/plain",
                    body.Length.ToString(CultureInfo.InvariantCulture));
                break;

            case ("GET", "/slow"):
            {
                // Headers immediately, then three delayed chunks.
                await WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    "Transfer-Encoding: chunked\r\n" +
                    "Connection: close\r\n\r\n");
                foreach (string chunk in new[] { "first", "second", "third" })
                {
                    await Task.Delay(300, _cts.Token);
                    await WriteAsync(stream, $"{chunk.Length:x}\r\n{chunk}\r\n");
                }
                await WriteAsync(stream, "0\r\n\r\n");
                break;
            }

            case ("GET", "/delay"):
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                await WriteSimpleAsync(stream, 200, "OK", "text/plain", "finally");
                break;

            case ("GET", "/redirect"):
                await WriteAsync(stream,
                    "HTTP/1.1 302 Found\r\n" +
                    "Location: /hello\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n");
                break;

            case ("GET", "/probe"):
                requestHeaders.TryGetValue("x-probe", out string? probe);
                await WriteSimpleAsync(stream, 200, "OK", "text/plain", "ok",
                    $"X-Probe-Echo: {probe ?? "<missing>"}");
                break;

            default:
                await WriteSimpleAsync(stream, 404, "Not Found", "text/plain", "nope");
                break;
        }
    }

    private static async Task WriteSimpleAsync(
        NetworkStream stream, int status, string reason, string contentType, string body,
        params string[] extraHeaders)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        foreach (string header in extraHeaders)
        {
            head.Append(header).Append("\r\n");
        }
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        await stream.FlushAsync();
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
