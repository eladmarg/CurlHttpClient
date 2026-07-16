using System.Collections.Concurrent;
using System.Net.Http;
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
    private readonly object _threadSync = new();
    private int _idleThreads;
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
        SemaphoreSlim? originGate = GetOriginGate(plan.Url);
        bool originAcquired = false, slotAcquired = false;
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
                    $"No worker became available within {_options.WorkerAdmissionTimeout.TotalSeconds:F0}s. " +
                    $"All {_options.MaxConcurrentRequests} concurrent requests are in flight — streaming " +
                    "responses hold a worker until their body is consumed or disposed. Increase " +
                    "CurlHttpClientOptions.MaxConcurrentRequests or consume/dispose response streams promptly.");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Ownership of the slot/origin gate passes to the worker.
            context.OnFinished = () =>
            {
                _active.TryRemove(context, out _);
                _slots.Release();
                originGate?.Release();
            };
            _active.TryAdd(context, 0);
            EnsureWorkerAvailable();
            _work.Add((context, plan), CancellationToken.None);
            slotAcquired = false;
            originAcquired = false;
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
        if (Volatile.Read(ref _idleThreads) > 0)
        {
            return;
        }
        lock (_threadSync)
        {
            if (_threads.Count >= _options.MaxConcurrentRequests)
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
            context.OnWorkerFinished();
            context.OnFinished?.Invoke();
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
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

        _work.Dispose();
        _slots.Dispose();
        foreach (SemaphoreSlim gate in _perOrigin.Values)
        {
            gate.Dispose();
        }
    }
}
