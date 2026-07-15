using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using CurlHttp.Diagnostics;
using CurlHttp.Native;

namespace CurlHttp.Internal;

/// <summary>
/// Per-request state hub. Lives from SendAsync until the native transfer has
/// fully finished AND the response stream has been consumed or disposed.
///
/// Threading map:
///  - SendAsync thread: constructs, prepares the body stream, awaits
///    <see cref="ResponseTask"/>.
///  - Worker thread: builds the native request, blocks in send; all native
///    callbacks (header/body/read/seek/debug) arrive on this thread.
///  - Any thread: <see cref="Cancel"/> (caller token, handler dispose,
///    response-stream dispose).
/// </summary>
internal sealed class CurlRequestContext : IDisposable
{
    private readonly CurlHttpClientOptions _options;
    private readonly HttpRequestMessage _request;
    private readonly TaskCompletionSource<HttpResponseMessage> _responseTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _bodyReadCts = new();
    private readonly ResponseHeaderParser _parser;
    private readonly CurlHttpEventSourceScope _events;

    private CurlBridgeRequestHandle? _nativeRequest;
    private GCHandle _selfHandle;
    private Stream? _requestBodyStream;
    private long _requestBodyStartPosition;
    private HttpResponseMessage? _response;
    private CancellationTokenRegistration _callerRegistration;

    private volatile Exception? _callbackException;
    private volatile bool _cancelRequested;
    private volatile bool _callerTokenTriggered;
    private volatile bool _streamDisposed;
    private int _completed;

    public CurlRequestContext(
        HttpRequestMessage request,
        CurlHttpClientOptions options,
        CancellationToken callerToken,
        CurlHttpEventSourceScope events,
        bool proxyAuthRetryPossible = false)
    {
        _request = request;
        _options = options;
        CallerToken = callerToken;
        _events = events;
        _parser = new ResponseHeaderParser(request.RequestUri!, options.AllowAutoRedirect,
            proxyAuthRetryPossible);
        BodyQueue = new BoundedByteQueue(options.MaxResponseBufferBytes);
    }

    public CancellationToken CallerToken { get; }
    public HttpRequestMessage Request => _request;

    /// <summary>Set by the dispatcher; releases pool/origin slots when the
    /// worker is completely done with this request.</summary>
    public Action? OnFinished { get; set; }
    public BoundedByteQueue BodyQueue { get; }
    public Task<HttpResponseMessage> ResponseTask => _responseTcs.Task;
    public bool CancelRequested => _cancelRequested;
    public Exception? CallbackException => _callbackException;

    /// <summary>Signals to the worker whether the request carries a body and
    /// its length: <c>long.MinValue</c> none, -1 unknown/streamed, >= 0 known.</summary>
    public long ContentLength { get; private set; } = long.MinValue;

    /// <summary>Opens the request content stream on the SendAsync thread so
    /// stream faults surface as clean managed exceptions before any native
    /// work starts.</summary>
    public async Task PrepareRequestBodyAsync(CancellationToken cancellationToken)
    {
        if (_request.Content is null)
        {
            // .NET sends Content-Length: 0 for bodyless POST/PUT; libcurl's
            // POST path does the same natively.
            ContentLength = _request.Method == HttpMethod.Post || _request.Method == HttpMethod.Put
                ? 0
                : long.MinValue;
            return;
        }

        _requestBodyStream = await _request.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        if (_requestBodyStream.CanSeek)
        {
            _requestBodyStartPosition = _requestBodyStream.Position;
        }

        if (_request.Headers.TransferEncodingChunked == true)
        {
            ContentLength = -1;
        }
        else
        {
            ContentLength = _request.Content.Headers.ContentLength ?? -1;
        }
    }

    public void RegisterCallerCancellation()
    {
        if (CallerToken.CanBeCanceled)
        {
            _callerRegistration = CallerToken.UnsafeRegister(
                static state =>
                {
                    var self = (CurlRequestContext)state!;
                    self._callerTokenTriggered = true;
                    self.Cancel();
                }, this);
        }
    }

    /// <summary>Requests prompt abortion of the transfer from any thread.
    /// Wakes: a producer blocked on the full body queue, a body reader
    /// blocked on the request stream, and (within ~1 s) libcurl's progress
    /// callback for the phases in between.</summary>
    public void Cancel()
    {
        _cancelRequested = true;
        try
        {
            _bodyReadCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        BodyQueue.Abort(CreateCancellationException());
        CurlBridgeRequestHandle? native = _nativeRequest;
        if (native is not null && !native.IsInvalid && !native.IsClosed)
        {
            try
            {
                NativeMethods.RequestCancel(native);
            }
            catch (ObjectDisposedException)
            {
                // The worker already tore the handle down; transfer is over.
            }
        }
    }

    public void OnResponseStreamDisposed()
    {
        _streamDisposed = true;
        if (Interlocked.CompareExchange(ref _completed, 0, 0) == 0)
        {
            // Transfer still running: disposing the stream aborts it.
            Cancel();
        }
        BodyQueue.Dispose();
    }

    /* ---------------- worker-thread entry points ---------------- */

    public void AttachNativeRequest(CurlBridgeRequestHandle handle)
    {
        _nativeRequest = handle;
        if (_cancelRequested)
        {
            NativeMethods.RequestCancel(handle);
        }
    }

    public IntPtr AllocateSelfHandle()
    {
        _selfHandle = GCHandle.Alloc(this);
        return GCHandle.ToIntPtr(_selfHandle);
    }

    /* ---------------- native callback targets (worker thread) ----------- */

    /// <summary>Body chunk from libcurl. Returns 0 to continue, 1 to abort.</summary>
    public int OnBodyData(ReadOnlySpan<byte> data)
    {
        if (_cancelRequested)
        {
            return 1;
        }
        if (_parser.OnBodyStarted())
        {
            PublishResponse();
        }
        return BodyQueue.Write(data) ? 0 : 1;
    }

    /// <summary>Raw header line from libcurl. Returns 0 to continue, 1 to abort.</summary>
    public int OnHeaderLine(ReadOnlySpan<byte> line, HeaderLineFlags flags, int blockStatus)
    {
        if (_cancelRequested)
        {
            return 1;
        }
        if (_parser.OnHeaderLine(line, flags, blockStatus))
        {
            PublishResponse();
        }
        return 0;
    }

    /// <summary>Request-body pull from libcurl. Returns bytes produced,
    /// 0 at EOF, -1 to abort.</summary>
    public long OnReadBody(Span<byte> destination)
    {
        if (_cancelRequested)
        {
            return -1;
        }
        Stream? stream = _requestBodyStream;
        if (stream is null)
        {
            return 0;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            // Async read blocked on this dedicated worker thread: correct for
            // async-only streams (e.g. ASP.NET request bodies with
            // AllowSynchronousIO=false) and wakeable through _bodyReadCts.
            int read = stream.ReadAsync(rented.AsMemory(0, destination.Length), _bodyReadCts.Token)
                .AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(destination);
            return read;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            TrySetCallbackException(new HttpRequestException(
                "The request content stream failed while being sent.", ex));
            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Rewind request for libcurl (auth retry / redirect resubmit).
    /// 0 ok, 1 cannot seek, 2 fail.</summary>
    public int OnSeekBody(long offset, int origin)
    {
        Stream? stream = _requestBodyStream;
        if (stream is null)
        {
            return 0;
        }
        if (!stream.CanSeek || origin != 0)
        {
            return 1;
        }
        try
        {
            stream.Position = _requestBodyStartPosition + offset;
            return 0;
        }
        catch (Exception ex)
        {
            TrySetCallbackException(ex);
            return 2;
        }
    }

    public void OnDebugLine(int kind, ReadOnlySpan<byte> data)
    {
        _events.OnNativeDebug(kind, data);
    }

    public void TrySetCallbackException(Exception exception)
    {
        _callbackException ??= exception;
    }

    /* ---------------- completion (worker thread) ---------------- */

    /// <summary>Called once when the blocking send returns.</summary>
    public void CompleteTransfer(
        CurlBridgeResult result,
        in BridgeResponseInfoNative info,
        string nativeError,
        string effectiveUrl)
    {
        Interlocked.Exchange(ref _completed, 1);

        if (result == CurlBridgeResult.Ok)
        {
            if (_parser.OnTransferCompleted())
            {
                PublishResponse();
            }
            if (_response is null && !_responseTcs.Task.IsCompleted)
            {
                var noHeaders = new HttpRequestException(
                    HttpRequestError.InvalidResponse,
                    "The transfer completed without producing a final response header block.");
                _responseTcs.TrySetException(noHeaders);
                BodyQueue.Fault(noHeaders);
                return;
            }
            ApplyTrailers();
            FinalizeResponseMetadata(info, effectiveUrl);
            BodyQueue.Complete();
            _events.OnCompleted(info.StatusCode, info.HttpVersion,
                connectionReused: info.NumConnects == 0, info.TotalTimeUs, info.RedirectCount);
            return;
        }

        _events.OnFailed(result.ToString(), info.CurlErrorCode, nativeError);

        Exception failure = CurlErrorMapper.Map(
            result, info.CurlErrorCode, nativeError, _callbackException,
            _callerTokenTriggered ? CallerToken : null,
            timedOut: result is CurlBridgeResult.Timeout,
            streamDisposed: _streamDisposed);

        if (!_responseTcs.Task.IsCompleted)
        {
            // Headers were never published: SendAsync gets the failure.
            _responseTcs.TrySetException(failure);
            BodyQueue.Fault(failure);
            return;
        }

        // Headers already delivered: the failure belongs to the body stream.
        if (result == CurlBridgeResult.Cancelled)
        {
            BodyQueue.Abort(failure);
        }
        else
        {
            BodyQueue.Fault(failure is IOException ? failure
                : new IOException("The response body transfer failed before completion.", failure));
        }
    }

    /// <summary>Worker fast-path: skip the native transfer entirely when the
    /// request was cancelled while queued.</summary>
    public bool TryFailFastIfCancelled()
    {
        if (!_cancelRequested)
        {
            return false;
        }
        Interlocked.Exchange(ref _completed, 1);
        Exception cancelled = CreateCancellationException();
        _responseTcs.TrySetException(cancelled);
        BodyQueue.Abort(cancelled);
        return true;
    }

    /// <summary>Worker-side failure outside the native send (marshaling,
    /// native request creation).</summary>
    public void FailTransfer(Exception exception)
    {
        Interlocked.Exchange(ref _completed, 1);
        _events.OnFailed(exception.GetType().Name, 0, exception.Message);
        _responseTcs.TrySetException(exception);
        BodyQueue.Fault(exception);
    }

    /// <summary>Cleanup after the worker is fully done with native resources.</summary>
    public void OnWorkerFinished()
    {
        _callerRegistration.Dispose();
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
        CurlBridgeRequestHandle? native = _nativeRequest;
        _nativeRequest = null;
        native?.Dispose();
        _bodyReadCts.Dispose();
    }

    /* ---------------- response assembly ---------------- */

    private void PublishResponse()
    {
        if (_response is not null)
        {
            return;
        }

        var response = new HttpResponseMessage((HttpStatusCode)_parser.StatusCode)
        {
            ReasonPhrase = _parser.ReasonPhrase,
            Version = _parser.HttpVersion2,
            RequestMessage = _request,
        };
        if (_parser.CurrentUri != _request.RequestUri)
        {
            // Match HttpClientHandler: after auto-redirects the request
            // message reflects the final URI.
            _request.RequestUri = _parser.CurrentUri;
        }

        response.Content = new CurlResponseContent(new CurlResponseStream(this));

        bool stripDecodedHeaders = _options.AutomaticDecompression && WasContentDecoded();
        foreach ((string name, string value) in _parser.Headers)
        {
            if (stripDecodedHeaders &&
                (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                // libcurl decoded the body transparently; these headers
                // describe bytes the caller will never see. Same behaviour
                // as HttpClientHandler.AutomaticDecompression.
                continue;
            }
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        _response = response;
        _events.OnHeadersReceived(_parser.StatusCode);
        _responseTcs.TrySetResult(response);
    }

    private bool WasContentDecoded()
    {
        foreach ((string name, string value) in _parser.Headers)
        {
            if (!name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string encoding = value.Trim();
            if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
                encoding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase) ||
                encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase) ||
                encoding.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void ApplyTrailers()
    {
        if (_response is null || _parser.Trailers.Count == 0)
        {
            return;
        }
        foreach ((string name, string value) in _parser.Trailers)
        {
            _response.TrailingHeaders.TryAddWithoutValidation(name, value);
        }
    }

    private void FinalizeResponseMetadata(in BridgeResponseInfoNative info, string effectiveUrl)
    {
        if (_response is null)
        {
            return;
        }
        // Authoritative values from libcurl once the transfer finished.
        _response.Version = info.HttpVersion switch
        {
            10 => HttpVersion.Version10,
            11 => HttpVersion.Version11,
            20 => HttpVersion.Version20,
            30 => HttpVersion.Version30,
            _ => _response.Version,
        };
        if (effectiveUrl.Length > 0 &&
            Uri.TryCreate(effectiveUrl, UriKind.Absolute, out Uri? effective) &&
            _request.RequestUri != effective)
        {
            _request.RequestUri = effective;
        }
    }

    private Exception CreateCancellationException()
    {
        if (_streamDisposed)
        {
            return new ObjectDisposedException(nameof(CurlResponseStream),
                "The response stream was disposed; the transfer has been aborted.");
        }
        return new TaskCanceledException(
            "The request was canceled.",
            null,
            _callerTokenTriggered ? CallerToken : CancellationToken.None);
    }

    public void Dispose()
    {
        BodyQueue.Dispose();
    }
}
