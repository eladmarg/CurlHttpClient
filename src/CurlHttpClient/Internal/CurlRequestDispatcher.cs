using System.Collections.Concurrent;
using CurlHttp.Native;

namespace CurlHttp.Internal;

/// <summary>Everything the worker needs to build the native request —
/// precomputed on the SendAsync path so the worker only marshals.</summary>
internal sealed record NativeRequestPlan(
    string Method,
    string Url,
    IReadOnlyList<string> HeaderLines,
    string Proxy,
    string? ProxyUserPassword,
    string RedirectProtocols,
    long ContentLength);

/// <summary>
/// Option A execution engine: a bounded pool of dedicated background threads,
/// each running one blocking native transfer end-to-end. Blocking these
/// threads is by design — it is what provides upload/download backpressure —
/// and they are never ThreadPool threads.
///
/// Capacity model: admission is gated by a semaphore sized to the pool, so at
/// most MaxConcurrentRequests transfers are in flight; a request whose
/// response body is still streaming continues to own its thread. Admission
/// beyond capacity fails fast after WorkerAdmissionTimeout.
/// </summary>
internal sealed class CurlRequestDispatcher : ICurlDispatcher
{
    private readonly CurlBridgeClientHandle _client;
    private readonly CurlHttpClientOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly BlockingCollection<(CurlRequestContext Context, NativeRequestPlan Plan)> _work = new();
    private readonly ConcurrentDictionary<CurlRequestContext, byte> _active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perOrigin = new();
    private readonly List<Thread> _threads = [];
    private readonly Lock _threadSync = new();
    // Cancelled by Dispose to wake requests parked on admission (the per-origin
    // gate has no timeout, so SemaphoreSlim.Dispose alone would strand them).
    private readonly CancellationTokenSource _disposeCts = new();
    private int _idleThreads;
    private int _disposeGuard;
    private volatile bool _disposed;

    public CurlRequestDispatcher(CurlBridgeClientHandle client, CurlHttpClientOptions options)
    {
        _client = client;
        _options = options;
        _slots = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
    }

    public async Task DispatchAsync(
        CurlRequestContext context, NativeRequestPlan plan, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SemaphoreSlim? originGate = GetOriginGate(plan.Url);
        bool originAcquired = false, slotAcquired = false;
        // Admission waits honor both the caller token and handler disposal.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCts.Token);
        CancellationToken admit = linked.Token;
        try
        {
            if (originGate is not null)
            {
                await originGate.WaitAsync(admit).ConfigureAwait(false);
                originAcquired = true;
            }
            slotAcquired = await _slots.WaitAsync(_options.WorkerAdmissionTimeout, admit)
                .ConfigureAwait(false);
            if (!slotAcquired)
            {
                throw new HttpRequestException(
                    $"No worker became available within {_options.WorkerAdmissionTimeout.TotalSeconds:F0}s. " +
                    $"All {_options.MaxConcurrentRequests} concurrent requests are in flight — streaming " +
                    "responses hold a worker until their body is consumed or disposed. Increase " +
                    "CurlHttpClientOptions.MaxConcurrentRequests or consume/dispose response streams promptly.");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Ownership of the slot/origin gate passes to the worker. Release is
            // tolerant: a straggler's OnFinished can run during/after Dispose.
            context.OnFinished = () =>
            {
                _active.TryRemove(context, out _);
                TryRelease(_slots);
                if (originGate is not null)
                {
                    TryRelease(originGate);
                }
            };
            _active.TryAdd(context, 0);
            EnsureWorkerAvailable();
            _work.Add((context, plan), CancellationToken.None);
            slotAcquired = false;
            originAcquired = false;
        }
        catch (OperationCanceledException) when (_disposed && !cancellationToken.IsCancellationRequested)
        {
            // Parked on admission when the handler was disposed (not a caller
            // cancel): surface as a disposal, not an opaque cancellation.
            throw new ObjectDisposedException(nameof(CurlHttpMessageHandler));
        }
        finally
        {
            if (slotAcquired)
            {
                TryRelease(_slots);
            }
            if (originAcquired && originGate is not null)
            {
                TryRelease(originGate);
            }
        }
    }

    /// <summary>Releases a semaphore, tolerating a race with Dispose. The
    /// semaphores are intentionally never disposed (see Dispose), so this only
    /// guards against an over-release bug turning into an unhandled throw.</summary>
    private static void TryRelease(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
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

    private void EnsureWorkerAvailable()
    {
        if (_disposed || Volatile.Read(ref _idleThreads) > 0)
        {
            return;
        }
        lock (_threadSync)
        {
            if (_disposed || _threads.Count >= _options.MaxConcurrentRequests)
            {
                return;
            }
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"CurlHttp-Worker-{_threads.Count}",
            };
            _threads.Add(thread);
            thread.Start();
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            (CurlRequestContext Context, NativeRequestPlan Plan) item;
            Interlocked.Increment(ref _idleThreads);
            try
            {
                if (!_work.TryTake(out item, Timeout.Infinite))
                {
                    return;
                }
            }
            catch (Exception) when (_disposed || _work.IsAddingCompleted)
            {
                return;
            }
            finally
            {
                Interlocked.Decrement(ref _idleThreads);
            }

            // If more work is already queued and no idle worker is left, the
            // racy idle-check in EnsureWorkerAvailable may have let two
            // dispatches share this one worker; spin a helper so the backlog
            // item is not stranded behind this (possibly long-streaming) one.
            if (_work.Count > 0)
            {
                EnsureWorkerAvailable();
            }

            RunRequest(item.Context, item.Plan);
        }
    }

    /// <summary>Runs one transfer end-to-end on this dedicated thread.</summary>
    private void RunRequest(CurlRequestContext context, NativeRequestPlan plan)
    {
        try
        {
            if (context.TryFailFastIfCancelled())
            {
                return;
            }
            using CurlBridgeRequestHandle native = NativeMethods.RequestCreate(_client);
            if (native.IsInvalid)
            {
                throw new CurlHttpInitializationException(
                    "curl_bridge_request_create failed (client shutting down?).");
            }
            context.AttachNativeRequest(native);

            Check(NativeMethods.RequestSetMethod(native, plan.Method));
            Check(NativeMethods.RequestSetUrl(native, plan.Url));
            foreach (string headerLine in plan.HeaderLines)
            {
                Check(NativeMethods.RequestAddHeader(native, headerLine));
            }
            Check(NativeMethods.RequestSetProxy(native, plan.Proxy, plan.ProxyUserPassword));
            Check(NativeMethods.RequestSetRedirectProtocols(native, plan.RedirectProtocols));
            if (plan.ContentLength != long.MinValue)
            {
                Check(NativeMethods.RequestSetBody(native, plan.ContentLength));
            }

            IntPtr contextHandle = context.AllocateSelfHandle();
            BridgeCallbacksNative callbacks = NativeCallbacks.Create(
                contextHandle,
                hasBody: plan.ContentLength != long.MinValue,
                verbose: _options.EnableNativeVerboseLogging);
            Check(NativeMethods.RequestSetCallbacks(native, in callbacks));

            BridgeResponseInfoNative info = BridgeResponseInfoNative.Create();
            CurlBridgeResult result = NativeMethods.RequestSend(native, ref info);

            string nativeError = result == CurlBridgeResult.Ok
                ? string.Empty
                : NativeMethods.RequestGetLastError(native);
            string effectiveUrl = NativeMethods.RequestGetEffectiveUrl(native);

            context.CompleteTransfer(result, in info, nativeError, effectiveUrl);
        }
        catch (Exception ex)
        {
            context.FailTransfer(ex);
        }
        finally
        {
            // This runs on a dedicated (non-ThreadPool) thread with no handler
            // above it, so an escaping exception would terminate the process.
            // Cleanup is already exception-safe, but guard defensively.
            try
            {
                context.OnWorkerFinished();
                context.OnFinished?.Invoke();
            }
            catch
            {
                // Nothing above can act on it; swallow to keep the worker alive.
            }
        }
    }

    private static void Check(CurlBridgeResult result)
    {
        if (result != CurlBridgeResult.Ok)
        {
            throw new HttpRequestException($"Native request configuration failed: {result}.");
        }
    }

    /// <summary>Stops admission, cancels all in-flight transfers, and joins
    /// the workers so the native client can be destroyed safely.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0)
        {
            return;
        }
        _disposed = true;
        _disposeCts.Cancel(); // wake anyone parked on admission
        _work.CompleteAdding();

        foreach (CurlRequestContext context in _active.Keys)
        {
            context.Cancel();
        }

        List<Thread> threads;
        lock (_threadSync)
        {
            threads = [.. _threads];
        }
        foreach (Thread thread in threads)
        {
            // Cancellation aborts transfers within ~1 s; allow a little slack.
            thread.Join(TimeSpan.FromSeconds(10));
        }

        // Fail any requests still queued but never picked up by a worker (the
        // worker loop exits on _disposed without draining) so their SendAsync
        // completes with a cancellation instead of hanging forever.
        while (_work.TryTake(out (CurlRequestContext Context, NativeRequestPlan Plan) item))
        {
            item.Context.TryFailFastIfCancelled();
            item.Context.OnFinished?.Invoke();
        }

        _work.Dispose();
        // _slots and the per-origin gates are deliberately NOT disposed: a
        // straggler worker that outran the join still calls Release() from its
        // finally, and disposing here would turn that into an
        // ObjectDisposedException on a dedicated thread (process crash). They
        // are small and reclaimed with the handler.
    }
}
