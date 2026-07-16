# Windows Server 2012 R2 deployment guide

## ⚠️ Runtime support caveat — read first

The **native** layer (curl_http_bridge.dll) targets the 6.3 kernel and is
fully compatible with Server 2012 R2 (v142 toolset, static CRT, Win 8.1-only
imports — build-gated). The opt-in `MultiEventLoop` engine adds a native loop
thread (`std::thread`/`std::mutex`, statically linked) and no new OS imports:
the import gate on the rebuilt DLL still shows only the Win 8.1-floor system
DLLs, so the on-target checklist below is unchanged for either engine.

The **managed** layer targets **.NET 10, which Microsoft does not support on
Windows Server 2012 R2** (the last supported release was .NET 8). This was an
explicit project decision. Consequences:

- The .NET 10 runtime may work on 2012 R2 today and break in any servicing
  update; Microsoft will not accept bug reports for that OS.
- The validation checklist below is therefore mandatory — including after
  every .NET runtime servicing update.
- Fallback: the library uses no net10-exclusive API surface that lacks a
  net8.0 equivalent; retargeting to net8.0 (officially supported on 2012 R2)
  is a small, low-risk change if the runtime proves unreliable.

## What to deploy

```
<app>/
  YourApp.dll, ...                      (framework-dependent) or self-contained output
  CurlHttpClient.dll
  runtimes/win-x64/native/
    curl_http_bridge.dll
    cacert.pem
```

- The process **must be x64**. The handler throws a clear
  `PlatformNotSupportedException` on x86/ARM64 processes.
- No VC++ redistributable is required (static CRT).
- Prefer a **self-contained** publish (`-r win-x64 --self-contained`) on
  2012 R2 so the app carries its own .NET runtime and is immune to
  machine-level runtime changes.
- If the native assets must live elsewhere, set
  `CurlHttpClientOptions.NativeLibraryPath` (absolute path). PATH and the
  working directory are never searched.

## Validation checklist (run on the real 2012 R2 machine)

Do not consider a deployment valid because it works on Windows 10/11.

1. **OS prerequisites**: fully patched 2012 R2 (KB2919355). No VC++ runtime
   install needed.
2. **Native smoke test** (no .NET involved — isolates OS compatibility):
   `curl_bridge_spike.exe` → must print the version JSON and
   `OK: OpenSSL backend ...`. Exit code 0.
3. **Native TLS fetch**: `curl_bridge_spike.exe https://<modern-tls-endpoint>`
   → `result=0 status=200`. Suggested probe: a TLS-1.3-only or
   modern-cipher-only endpoint that Schannel on this OS cannot reach —
   proving the whole point.
4. **.NET runtime smoke test**: `dotnet --info`, then run the sample:
   `CurlHttpClient.Sample.exe` → diagnostics must show
   `TLS backend: OpenSSL/3.x (…)  (OpenSSL: True)` and a 200 response.
   **This is the go/no-go step for the unsupported-runtime risk.**
5. **Certificate validation is alive**: `CurlHttpClient.Sample.exe
   https://expired.badssl.com/` (or any untrusted endpoint) → must FAIL with
   a secure-connection error. If it succeeds, stop and investigate — never
   ship a build that skips verification.
6. **Application-level check**: call your health endpoint that asserts
   `handler.GetDiagnostics().TlsBackendIsOpenSsl == true`.
7. **Under load**: run a brief soak (parallel requests + one long streaming
   response) and observe: worker threads named `CurlHttp-Worker-*` stay
   bounded, memory stays flat (bounded response buffering), connections are
   reused (EventSource `connectionReused=true` on repeat requests).
8. **Repeat steps 2–6 after**: any Windows update on the host, any .NET
   servicing update, any rebuild of the native bridge.

## IHttpClientFactory registration

```csharp
services.AddCurlHttpClient("ModernTlsClient", _ => new CurlHttpClientOptions
{
    ConnectTimeout = TimeSpan.FromSeconds(15),
    AllowAutoRedirect = true,
});
// or, with explicit control:
services.AddHttpClient("ModernTlsClient")
    .ConfigurePrimaryHttpMessageHandler(sp => new CurlHttpMessageHandler(
        options, sp.GetRequiredService<ILogger<CurlHttpMessageHandler>>()))
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);   // REQUIRED — see below
```

`SetHandlerLifetime(Timeout.InfiniteTimeSpan)` is required: the factory's
default 2-minute handler rotation would repeatedly discard the native
connection pools. DNS staleness is already handled by
`PooledConnectionLifetime` (default 15 min).
