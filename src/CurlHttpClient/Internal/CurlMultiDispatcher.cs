using System.Collections.Concurrent;
using System.Net.Http;
using CurlHttp.Native;

namespace CurlHttp.Internal;

/// <summary>
/// Event-loop engine dispatcher: submits each request to the native
/// curl_multi loop and awaits its completion (delivered by the native
/// on_complete callback via <see cref="CurlRequestContext.CompleteFromNative"/>).
/// No per-request thread; connections are shared across all requests and
/// HTTP/2 streams multiplex.
///
/// Admission is still gated (per-origin + optional global) so a burst cannot
/// submit unbounded work to the loop; unlike the worker engine, a submitted
/// request does not occupy a thread, so the gate exists only to bound
/// in-flight transfers, not threads.
/// </summary>
internal sealed class CurlMultiDispatcher : ICurlDispatcher
{
    private readonly CurlBridgeMultiClientHandle _client;
    private readonly CurlHttpClientOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perOrigin = new();
    private readonly ConcurrentDictionary<CurlRequestContext, byte> _active = new();
    private volatile bool _disposed;

    public CurlMultiDispatcher(CurlBridgeMultiClientHandle client, CurlHttpClientOptions options)
    {
        _client = client;
        _options = options;
        // Bounds concurrent in-flight transfers (not threads); generous.
        _slots = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
    }

    public async Task DispatchAsync(
        CurlRequestContext context, NativeRequestPlan plan, CancellationToken cancellationToken)
    {
        SemaphoreSlim? originGate = GetOriginGate(plan.Url);
        bool originAcquired = false, slotAcquired = false;
        CurlBridgeRequestHandle? request = null;
        try
        {
            if (originGate is not null)
            {
                await originGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                originAcquired = true;
            }
            slotAcquired = await _slots.WaitAsync(_options.WorkerAdmissionTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!slotAcquired)
            {
                throw new HttpRequestException(
                    $"No transfer slot became available within {_options.WorkerAdmissionTimeout.TotalSeconds:F0}s " +
                    $"(MaxConcurrentRequests={_options.MaxConcurrentRequests}).");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);

            request = NativeMethods.MultiRequestCreate(_client);
            if (request.IsInvalid)
            {
                throw new CurlHttpInitializationException(
                    "curl_bridge_multi_request_create failed (engine shutting down?).");
            }

            Check(NativeMethods.RequestSetMethod(request, plan.Method));
            Check(NativeMethods.RequestSetUrl(request, plan.Url));
            foreach (string headerLine in plan.HeaderLines)
            {
                Check(NativeMethods.RequestAddHeader(request, headerLine));
            }
            Check(NativeMethods.RequestSetProxy(request, plan.Proxy, plan.ProxyUserPassword));
            Check(NativeMethods.RequestSetRedirectProtocols(request, plan.RedirectProtocols));
            if (plan.ContentLength != long.MinValue)
            {
                Check(NativeMethods.RequestSetBody(request, plan.ContentLength));
            }

            IntPtr contextHandle = context.AllocateSelfHandle();
            BridgeCallbacksNative callbacks = NativeCallbacks.Create(
                contextHandle,
                hasBody: plan.ContentLength != long.MinValue,
                verbose: _options.EnableNativeVerboseLogging,
                multi: true);
            Check(NativeMethods.RequestSetCallbacks(request, in callbacks));

            context.AttachNativeRequest(request);
            context.EnableMultiMode(_client, request, _options.UploadBufferSize);

            // The native completion callback drives cleanup; release the
            // admission slots and the request handle there.
            CurlBridgeRequestHandle owned = request;
            _active.TryAdd(context, 0);
            context.OnFinished = () =>
            {
                _active.TryRemove(context, out _);
                owned.Dispose();
                _slots.Release();
                originGate?.Release();
            };

            CurlBridgeResult submit = NativeMethods.MultiSubmit(_client, request);
            if (submit != CurlBridgeResult.Ok)
            {
                // on_complete will not fire; cleanup stays with SendAsync.
                throw new HttpRequestException(
                    $"Failed to submit the request to the event loop: {submit}.");
            }

            // Ownership of the admission slots and the request handle has passed
            // to OnFinished (it fires on native completion). Transfer it BEFORE
            // anything that can throw, so the finally cannot double-release a
            // slot that OnFinished already released.
            slotAcquired = false;
            originAcquired = false;
            request = null;

            // If the caller cancelled during setup, nudge the loop. A fast
            // completion may already have disposed the handle via OnFinished —
            // tolerate that; the transfer is finishing regardless.
            if (context.CancelRequested)
            {
                try
                {
                    NativeMethods.MultiCancel(_client, owned);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            // Do NOT await ResponseTask here: SendAsync awaits it and marks the
            // request dispatched, so its finally skips the worker-cleanup path.
            // Awaiting here would let a pre-header failure fault back into
            // SendAsync as "not dispatched", running OnWorkerFinished on this
            // thread concurrently with the loop thread's OnMultiFinished.
        }
        finally
        {
            if (slotAcquired)
            {
                _slots.Release();
            }
            if (originAcquired)
            {
                originGate?.Release();
            }
            // Only reached on a pre-submit failure (submit hands ownership off).
            request?.Dispose();
        }
    }

    private SemaphoreSlim? GetOriginGate(string url)
    {
        if (_options.MaxConnectionsPerServer <= 0)
        {
            return null;
        }
        var uri = new Uri(url);
        string key = $"{uri.Scheme}|{uri.Host}|{uri.Port}";
        return _perOrigin.GetOrAdd(key,
            _ => new SemaphoreSlim(_options.MaxConnectionsPerServer, _options.MaxConnectionsPerServer));
    }

    private static void Check(CurlBridgeResult result)
    {
        if (result != CurlBridgeResult.Ok)
        {
            throw new HttpRequestException($"Native request configuration failed: {result}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Cancel in-flight transfers; the handler then disposes the native
        // client, whose destroy stops the loop thread and delivers the
        // completion callback (CANCELLED) for everything still queued.
        foreach (CurlRequestContext context in _active.Keys)
        {
            context.Cancel();
        }
        _slots.Dispose();
        foreach (SemaphoreSlim gate in _perOrigin.Values)
        {
            gate.Dispose();
        }
    }
}
