# CurlHttpClient.Consumer

A standalone solution that consumes **CurlHttpClient as a real application
would**: via the packed NuGet package (not a project reference), using only
the standard `HttpClient` API.

What it proves beyond the main repo's test suites:

- The NuGet package is complete: `curl_http_bridge.dll` and `cacert.pem`
  flow to a consumer's output through the `runtimes/win-x64/native`
  conventions and are resolved automatically at runtime (asserted by
  `PackagedNativeAssets_ResolveAndProveOpenSsl`).
- Everyday `HttpClient` usage works with zero library-specific code beyond
  handler construction: GET/POST, streaming with `ResponseHeadersRead`,
  prompt cancellation, redirects, custom headers, concurrency.
- `RealWorldTlsTests` (trait `Category=RequiresInternet`) hits a live
  endpoint and asserts **TLS 1.3** was negotiated through the bundled
  OpenSSL with the packaged Mozilla trust store.

## Running

```cmd
:: 1. produce the package the solution consumes
build\package.cmd            (from the repo root)

:: 2. run the consumer tests
cd consumer
dotnet test

:: offline runs: exclude the internet-dependent test
dotnet test --filter "Category!=RequiresInternet"
```

Package resolution is pinned by `nuget.config` to `..\artifacts` with a
solution-local package cache (`consumer\packages`). After rebuilding the
package at the same version, delete `consumer\packages` so the new binaries
are restored.

The local HTTP endpoints are served by `MiniHttpServer` — a dependency-free
raw-`TcpListener` server, so this solution needs nothing but the package
under test and xunit.
