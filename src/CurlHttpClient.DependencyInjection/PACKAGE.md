# CurlHttpClient.DependencyInjection

`IHttpClientFactory` / dependency-injection integration for
[CurlHttpClient](https://www.nuget.org/packages/CurlHttpClient) — an
`HttpMessageHandler` that gives `HttpClient` modern TLS 1.2/1.3 through a
bundled libcurl + OpenSSL native bridge (for hosts whose Schannel cannot, e.g.
Windows Server 2012 R2). Windows x64.

```csharp
// Registers a named HttpClient whose primary handler is the curl transport.
// Returns an IHttpClientBuilder, so you can chain the usual factory config.
services.AddCurlHttpClient("modern-tls", _ => new CurlHttpClientOptions
{
    EnableHttp2 = true,
    // CertificateAuthorityBundlePath = ...,
});

// Resolve it via IHttpClientFactory:
HttpClient client = httpClientFactory.CreateClient("modern-tls");
```

The handler lifetime is set to infinite by design — it is long-lived (its
native connection pools live inside it), and staleness is bounded by
`CurlHttpClientOptions.PooledConnectionLifetime`.

MIT licensed. See the repository for full documentation.
