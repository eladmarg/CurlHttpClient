using System.Net;

namespace CurlHttp.Internal;

/// <summary>
/// HttpContent over the live native transfer. Never buffers the body itself:
/// with HttpCompletionOption.ResponseHeadersRead the caller streams directly;
/// with the default completion option HttpClient calls LoadIntoBufferAsync,
/// which drives SerializeToStreamAsync below.
/// </summary>
internal sealed class CurlResponseContent : HttpContent
{
    private readonly CurlResponseStream _stream;
    private bool _consumed;

    public CurlResponseContent(CurlResponseStream stream)
    {
        _stream = stream;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => Task.FromResult<Stream>(_stream);

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => Task.FromResult<Stream>(_stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (_consumed)
        {
            throw new InvalidOperationException("The response content has already been consumed.");
        }
        _consumed = true;
        await _stream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
