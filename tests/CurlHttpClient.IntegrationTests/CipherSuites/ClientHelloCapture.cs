using System.Net;
using System.Net.Sockets;

namespace CurlHttp.IntegrationTests.CipherSuites;

/// <summary>
/// Captures the raw TLS ClientHello our handler puts on the wire and parses
/// the offered cipher-suite code points — ground truth for "does the client
/// offer suite X", independent of any server's willingness to negotiate it.
/// </summary>
internal static class ClientHelloCapture
{
    /// <summary>Points the handler at a throwaway listener, records the first
    /// TLS record, and returns the offered cipher suites in offer order.
    /// The doomed request's failure is swallowed.</summary>
    public static async Task<IReadOnlyList<ushort>> CaptureOfferedSuitesAsync(
        CurlHttpClientOptions options)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<byte[]> recordTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();

            byte[] header = await ReadExactAsync(stream, 5);
            if (header[0] != 0x16) // handshake record
            {
                throw new InvalidOperationException($"not a TLS handshake record: 0x{header[0]:x2}");
            }
            int recordLength = (header[3] << 8) | header[4];
            byte[] payload = await ReadExactAsync(stream, recordLength);
            return payload;
            // connection drops here; the client sees a handshake failure
        });

        using (var handler = new CurlHttpMessageHandler(options))
        using (var httpClient = new HttpClient(handler))
        {
            try
            {
                using HttpResponseMessage _ = await httpClient.GetAsync(
                    $"https://127.0.0.1:{port}/", new CancellationTokenSource(10_000).Token);
            }
            catch (HttpRequestException)
            {
                // expected: the "server" never answers the hello
            }
            catch (OperationCanceledException)
            {
            }
        }

        byte[] hello = await recordTask.WaitAsync(TimeSpan.FromSeconds(10));
        return ParseCipherSuites(hello);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                throw new EndOfStreamException("connection closed inside the ClientHello");
            }
            offset += read;
        }
        return buffer;
    }

    /// <summary>Parses the handshake payload of a ClientHello
    /// (RFC 8446 §4.1.2 layout, identical framing in TLS 1.2).</summary>
    internal static IReadOnlyList<ushort> ParseCipherSuites(ReadOnlySpan<byte> handshake)
    {
        if (handshake.Length < 4 || handshake[0] != 0x01) // client_hello
        {
            throw new InvalidOperationException("payload is not a ClientHello");
        }
        int offset = 4;      // msg_type(1) + length(3)
        offset += 2;         // legacy_version
        offset += 32;        // random
        int sessionIdLength = handshake[offset];
        offset += 1 + sessionIdLength;

        int suitesLength = (handshake[offset] << 8) | handshake[offset + 1];
        offset += 2;

        var suites = new List<ushort>(suitesLength / 2);
        for (int i = 0; i < suitesLength; i += 2)
        {
            suites.Add((ushort)((handshake[offset + i] << 8) | handshake[offset + i + 1]));
        }
        return suites;
    }
}
