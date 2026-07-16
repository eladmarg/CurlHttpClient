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
    // Only allocated when the request has a body (interrupts the blocking
    // upload read); bodyless GETs pay nothing. Cancellation of a bodyless
    // request still works via _cancelRequested + BodyQueue.Abort + native cancel.
    private CancellationTokenSource? _bodyReadCts;
    private readonly ResponseHeaderParser _parser;
    private readonly CurlHttpEventSourceScope _events;

    private CurlBridgeRequestHandle? _nativeRequest;
    private GCHandle _selfHandle;
    private Stream? _requestBodyStream;
    private byte[]? _uploadBuffer;
    private long _requestBodyStartPosition;
    private HttpResponseMessage? _response;
    private CancellationTokenRegistration _callerRegistration;

    // Event-loop engine only. volatile: EnableMultiMode is written on the
    // dispatch thread but these are read by Cancel()/SafeUnpause on arbitrary
    // cancel threads; a stale read would route cancellation to the worker path
    // (flag only) and never wake a paused multi transfer.
    private volatile bool _multiMode;
    private volatile CurlBridgeMultiClientHandle? _multiClient;
    private volatile CurlBridgeRequestHandle? _multiRequestHandle;
    private BoundedByteQueue? _uploadQueue;
    private Task? _uploadPump;

    private int _resourcesReleased;
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
            proxyAuthRetryPossible, options.MaxResponseHeadersLength);
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
        // Created before caller-cancel registration and dispatch, so no window
        // exists where a body is present but the CTS is null.
        _bodyReadCts = new CancellationTokenSource();
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
            _bodyReadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        BodyQueue.Abort(CreateCancellationException());

        if (_multiMode)
        {
            // Instant: wakes the loop thread, which removes the handle.
            SafeUnpause(NativeMethods.MultiCancel);
            return;
        }
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

    /* ---------------- event-loop engine ---------------- */

    /// <summary>Switches this context to non-blocking (event-loop) behavior:
    /// the write callback pauses instead of blocking, and — when the request
    /// has a body — an async pump feeds a bounded upload queue that the read
    /// callback drains without blocking the loop thread.</summary>
    public void EnableMultiMode(
        CurlBridgeMultiClientHandle client, CurlBridgeRequestHandle requestHandle, int uploadBufferBytes)
    {
        _multiMode = true;
        _multiClient = client;
        _multiRequestHandle = requestHandle;
        BodyQueue.SetSpaceAvailableCallback(RequestUnpauseWrite);

        // The pump exists only for non-seekable bodies. A seekable body
        // (ByteArrayContent/StringContent/JsonContent → MemoryStream) is read
        // directly in OnReadBody so libcurl can rewind and resend it on an
        // auth/redirect (a 307/308 POST); a one-shot pump queue cannot rewind.
        if (ContentLength != long.MinValue && _requestBodyStream is { CanSeek: false })
        {
            _uploadQueue = new BoundedByteQueue(Math.Max(uploadBufferBytes, 64 * 1024) * 2);
            _uploadQueue.SetDataAvailableCallback(RequestUnpauseRead);
            _uploadPump = Task.Run(PumpUploadAsync);
        }
    }

    private void RequestUnpauseWrite() => SafeUnpause(NativeMethods.MultiUnpauseWrite);

    private void RequestUnpauseRead() => SafeUnpause(NativeMethods.MultiUnpauseRead);

    private void SafeUnpause(Action<CurlBridgeMultiClientHandle, CurlBridgeRequestHandle> unpause)
    {
        CurlBridgeMultiClientHandle? client = _multiClient;
        CurlBridgeRequestHandle? request = _multiRequestHandle;
        if (client is null || request is null || client.IsClosed || request.IsClosed)
        {
            return;
        }
        try
        {
            unpause(client, request);
        }
        catch (ObjectDisposedException)
        {
            // The transfer already finished; nothing to resume.
        }
    }

    private async Task PumpUploadAsync()
    {
        Stream stream = _requestBodyStream!;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(
                buffer, _bodyReadCts?.Token ?? CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                // Blocks this pump task (not the loop thread) when the upload
                // buffer is full — bounded memory. Returns false on abort.
                if (!_uploadQueue!.Write(buffer.AsSpan(0, read)))
                {
                    return;
                }
            }
            _uploadQueue!.Complete();
        }
        catch (OperationCanceledException)
        {
            _uploadQueue!.Abort();
        }
        catch (Exception ex)
        {
            _uploadQueue!.Fault(new HttpRequestException(
                "The request content stream failed while being sent.", ex));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Cleanup after the native completion callback (event-loop
    /// engine). Runs on the loop thread; must not block it for long.</summary>
    public void OnMultiFinished()
    {
        _uploadQueue?.Abort(); // wake a pump blocked writing to a full queue
        ReleasePerRequestResources();
    }

    /// <summary>Frees the per-request GCHandle, pooled upload buffer, body-read
    /// CTS and caller registration exactly once, no matter how many completion
    /// paths reach it. The event-loop and worker engines free on different
    /// threads (loop thread vs the SendAsync continuation on a pre-header
    /// failure), so this must be race-safe: an unfenced double free could
    /// release a GCHandle slot already reallocated to another live request or
    /// return one pooled array to two owners.</summary>
    private void ReleasePerRequestResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
        {
            return;
        }
        // Unregister, not Dispose: Dispose() blocks until a concurrently
        // running callback completes. This cleanup can run on the completion
        // thread while the caller-token callback (which calls Cancel()) is
        // still executing on another thread; waiting for it risks a stall.
        // Cancel() is idempotent, so we do not need to wait for it.
        _callerRegistration.Unregister();
        // Cancel (not just dispose) so an upload pump parked in the content
        // stream's ReadAsync is interrupted rather than orphaned.
        try
        {
            _bodyReadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
        if (_uploadBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_uploadBuffer);
            _uploadBuffer = null;
        }
        _bodyReadCts?.Dispose();
    }

    /* ---------------- native callback targets (worker thread) ----------- */

    /// <summary>Body chunk from libcurl. Returns 0 to continue, 1 to abort,
    /// 2 to pause (event-loop engine, buffer full).</summary>
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
        if (_multiMode)
        {
            // Non-blocking: pause the transfer when the buffer is full; the
            // consumer's drain triggers the unpause via the space-available
            // callback wired in EnableMultiMode. A false return can also mean
            // the queue was aborted (cancel/dispose) — abort the transfer then
            // rather than pausing it forever.
            if (BodyQueue.TryWrite(data))
            {
                return 0;
            }
            return BodyQueue.IsAborted ? 1 : 2;
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
    /// 0 at EOF, -1 to abort, -2 to pause (event-loop engine, no bytes ready).</summary>
    public long OnReadBody(Span<byte> destination)
    {
        if (_cancelRequested)
        {
            return -1;
        }
        if (_multiMode && _uploadQueue is not null)
        {
            // Non-seekable body: non-blocking drain of the pump-fed upload
            // queue; pause (return -2) when empty.
            try
            {
                int n = _uploadQueue.TryReadUpload(destination);
                return n == -1 ? -2 : n;
            }
            catch (Exception ex)
            {
                TrySetCallbackException(ex is HttpRequestException
                    ? ex
                    : new HttpRequestException("The request content stream failed while being sent.", ex));
                return -1;
            }
        }
        // Worker engine, or multi engine with a seekable body (read directly so
        // libcurl can rewind and resend on an auth/redirect). Seekable managed
        // bodies (MemoryStream-backed content) complete synchronously, so this
        // does not block the loop thread in practice.
        return ReadBodyDirect(destination);
    }

    private long ReadBodyDirect(Span<byte> destination)
    {
        Stream? stream = _requestBodyStream;
        if (stream is null)
        {
            return 0;
        }
        // One buffer reused across all read callbacks for this request (curl
        // asks for the same upload-buffer size each time), rather than a
        // rent/return per callback. Returned in OnWorkerFinished/OnMultiFinished.
        if (_uploadBuffer is null || _uploadBuffer.Length < destination.Length)
        {
            // Null the field before returning the old buffer so that if Rent
            // throws (OOM) the cleanup paths do not return an array we already
            // returned (a double-return corrupts the shared pool).
            if (_uploadBuffer is not null)
            {
                byte[] old = _uploadBuffer;
                _uploadBuffer = null;
                ArrayPool<byte>.Shared.Return(old);
            }
            _uploadBuffer = ArrayPool<byte>.Shared.Rent(destination.Length);
        }
        try
        {
            // Async read blocked on this dedicated thread: correct for
            // async-only streams (e.g. ASP.NET request bodies with
            // AllowSynchronousIO=false) and wakeable through _bodyReadCts. The
            // fast path avoids a Task allocation when the stream (e.g. a
            // MemoryStream from ByteArrayContent) completes synchronously.
            ValueTask<int> readOp = stream.ReadAsync(
                _uploadBuffer.AsMemory(0, destination.Length),
                _bodyReadCts?.Token ?? CancellationToken.None);
            int read = readOp.IsCompletedSuccessfully
                ? readOp.Result
                : readOp.AsTask().GetAwaiter().GetResult();
            _uploadBuffer.AsSpan(0, read).CopyTo(destination);
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
    /// <summary>Completion entry point for the event-loop engine, invoked from
    /// the native on_complete callback on the loop thread. Reads the request's
    /// error/effective-url, completes the transfer, and cleans up.</summary>
    public void CompleteFromNative(CurlBridgeResult result, in BridgeResponseInfoNative info)
    {
        string nativeError = result == CurlBridgeResult.Ok || _multiRequestHandle is null
            ? string.Empty
            : NativeMethods.RequestGetLastError(_multiRequestHandle);
        string effectiveUrl = _multiRequestHandle is null
            ? string.Empty
            : NativeMethods.RequestGetEffectiveUrl(_multiRequestHandle);

        CompleteTransfer(result, in info, nativeError, effectiveUrl);
        OnMultiFinished();
        OnFinished?.Invoke(); // dispatcher: release slots + dispose the request handle
    }

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

    /// <summary>Cleanup after the worker is fully done with native resources,
    /// or on the SendAsync path when the request was never dispatched.</summary>
    public void OnWorkerFinished()
    {
        ReleasePerRequestResources();
        CurlBridgeRequestHandle? native = _nativeRequest;
        _nativeRequest = null;
        native?.Dispose();
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
