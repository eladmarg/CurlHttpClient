using System.Net;
using System.Text;
using CurlHttp.Native;

namespace CurlHttp.Internal;

/// <summary>
/// Assembles response header blocks from the raw header lines libcurl
/// delivers, and decides when a block is the FINAL response (the one that
/// becomes the caller's HttpResponseMessage).
///
/// libcurl forwards every header block of the transfer through the same
/// callback: informational 1xx responses, each hop of a followed redirect
/// chain, and trailers (proxy CONNECT responses are suppressed natively).
/// The finality rules are deliberately conservative:
///
///  - 1xx blocks are consumed and discarded.
///  - Trailer lines (arriving after the first body byte) are collected
///    separately for HttpResponseMessage.TrailingHeaders.
///  - A completed block is IMMEDIATELY final when nothing can supersede it:
///    any 2xx/4xx/5xx, or a 3xx when redirects are disabled or without a
///    Location header. This is what makes ResponseHeadersRead return before
///    the body arrives.
///  - A completed 3xx block with a Location header while redirects are
///    enabled is AMBIGUOUS (libcurl may or may not follow it, e.g. redirect
///    budget, protocol restrictions). It is parked; the next status line
///    discards it, and the first body byte or the end of the transfer
///    promotes it to final. Safe because libcurl never delivers body bytes
///    for a response it acts on.
/// </summary>
internal sealed class ResponseHeaderParser
{
    private readonly bool _followRedirects;
    private readonly List<(string Name, string Value)> _headers = [];
    private readonly List<(string Name, string Value)> _trailers = [];

    private int _statusCode;
    private string _reasonPhrase = string.Empty;
    private Version _httpVersion = HttpVersion.Version11;
    private bool _blockOpen;
    private bool _finalized;

    private Uri _currentUri;

    public ResponseHeaderParser(Uri requestUri, bool followRedirects)
    {
        _currentUri = requestUri;
        _followRedirects = followRedirects;
    }

    /// <summary>Set when a completed block is known to be the final response.
    /// The owner turns this into the HttpResponseMessage.</summary>
    public bool HasFinalBlock { get; private set; }

    public int StatusCode => _statusCode;
    public string ReasonPhrase => _reasonPhrase;
    public Version HttpVersion2 => _httpVersion;
    public IReadOnlyList<(string Name, string Value)> Headers => _headers;
    public IReadOnlyList<(string Name, string Value)> Trailers => _trailers;

    /// <summary>Final URI after managed-tracked redirects.</summary>
    public Uri CurrentUri => _currentUri;

    /// <summary>Processes one raw header line. Returns true when this line
    /// completed the FINAL header block (the caller should publish the
    /// response now).</summary>
    public bool OnHeaderLine(ReadOnlySpan<byte> line, HeaderLineFlags flags, int blockStatus)
    {
        // Header bytes are decoded as Latin-1 per RFC 9110's historical
        // guidance; UTF-8 would corrupt bytes >= 0x80.
        string text = Encoding.Latin1.GetString(TrimCrLf(line));

        // Trailers arrive after the final block was already published, so
        // this must run before the finalized short-circuit below.
        if ((flags & HeaderLineFlags.Trailer) != 0)
        {
            if (text.Length > 0 && (flags & HeaderLineFlags.StatusLine) == 0 &&
                TrySplitHeader(text, out string tn, out string tv))
            {
                _trailers.Add((tn, tv));
            }
            return false;
        }

        if (_finalized)
        {
            return false;
        }

        if ((flags & HeaderLineFlags.StatusLine) != 0)
        {
            // A new block supersedes any parked ambiguous block: that block
            // was an intermediate hop.
            _headers.Clear();
            _blockOpen = true;
            HasFinalBlock = false;
            ParseStatusLine(text, blockStatus);
            return false;
        }

        if ((flags & HeaderLineFlags.BlockEnd) != 0)
        {
            if (!_blockOpen)
            {
                return false;
            }
            _blockOpen = false;

            if ((flags & HeaderLineFlags.Informational) != 0 || _statusCode is >= 100 and < 200)
            {
                _headers.Clear();
                return false;
            }

            if (_statusCode is >= 300 and < 400 && _followRedirects &&
                TryGetHeader("Location", out string? location))
            {
                // Ambiguous: libcurl may follow. Track the prospective URI so
                // RequestMessage.RequestUri can reflect the final location.
                if (Uri.TryCreate(_currentUri, location, out Uri? next))
                {
                    _currentUri = next;
                }
                HasFinalBlock = true; // parked; promoted by body/completion
                return false;
            }

            HasFinalBlock = true;
            _finalized = true;
            return true;
        }

        if (_blockOpen && text.Length > 0 && TrySplitHeader(text, out string name, out string value))
        {
            _headers.Add((name, value));
        }
        return false;
    }

    /// <summary>First body byte arrived: whatever block we hold is final.</summary>
    public bool OnBodyStarted() => PromoteParkedBlock();

    /// <summary>Transfer finished without body bytes: promote a parked block.</summary>
    public bool OnTransferCompleted() => PromoteParkedBlock();

    private bool PromoteParkedBlock()
    {
        if (_finalized || !HasFinalBlock)
        {
            return false;
        }
        _finalized = true;
        return true;
    }

    private void ParseStatusLine(string line, int blockStatus)
    {
        _statusCode = blockStatus;
        _reasonPhrase = string.Empty;
        _httpVersion = HttpVersion.Version11;

        // "HTTP/1.1 200 OK" | "HTTP/2 200"
        if (!line.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return;
        }
        int space = line.IndexOf(' ');
        string versionToken = space > 0 ? line[5..space] : line[5..];
        _httpVersion = versionToken switch
        {
            "1.0" => HttpVersion.Version10,
            "1.1" => HttpVersion.Version11,
            "2" or "2.0" => HttpVersion.Version20,
            "3" or "3.0" => HttpVersion.Version30,
            _ => HttpVersion.Version11,
        };
        if (space < 0)
        {
            return;
        }
        ReadOnlySpan<char> rest = line.AsSpan(space + 1).TrimStart(' ');
        int statusEnd = rest.IndexOf(' ');
        if (statusEnd >= 0)
        {
            _reasonPhrase = rest[(statusEnd + 1)..].ToString();
        }
        if (_statusCode == 0 && int.TryParse(
                statusEnd >= 0 ? rest[..statusEnd] : rest, out int parsed))
        {
            _statusCode = parsed;
        }
    }

    private bool TryGetHeader(string name, out string? value)
    {
        foreach ((string n, string v) in _headers)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool TrySplitHeader(string line, out string name, out string value)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }
        name = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return name.Length > 0;
    }

    private static ReadOnlySpan<byte> TrimCrLf(ReadOnlySpan<byte> line)
    {
        while (line.Length > 0 && (line[^1] == (byte)'\r' || line[^1] == (byte)'\n'))
        {
            line = line[..^1];
        }
        return line;
    }
}
