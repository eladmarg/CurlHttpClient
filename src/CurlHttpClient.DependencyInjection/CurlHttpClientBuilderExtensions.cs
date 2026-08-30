using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CurlHttp.DependencyInjection;

/// <summary>
/// Attaches the libcurl + OpenSSL transport to an <see cref="HttpClient"/>
/// registration that ALREADY exists — a typed client, a Refit client, or a
/// client an SDK registered on your behalf. This is usually what an adopter
/// needs: the goal is to change the transport under clients the application
/// already declares, not to introduce a new one.
///
/// Use <see cref="CurlHttpClientServiceCollectionExtensions.AddCurlHttpClient(IServiceCollection, string, Func{IServiceProvider, CurlHttpClientOptions})"/>
/// instead when you are declaring a brand-new named client.
/// </summary>
public static class CurlHttpClientBuilderExtensions
{
    /// <summary>
    /// Routes this client's traffic through a <see cref="CurlHttpMessageHandler"/>.
    ///
    /// The handler lifetime is set to infinite: the handler is designed to be
    /// long-lived (its native connection pools live inside it) and connection
    /// staleness is already bounded by
    /// <see cref="CurlHttpClientOptions.PooledConnectionLifetime"/>. The
    /// factory's default 2-minute rotation would discard native pools for no
    /// benefit. Forgetting this is the easiest mistake to make when wiring the
    /// handler up by hand.
    /// </summary>
    /// <example>
    /// services.AddHttpClient&lt;IInvoiceApi, InvoiceApi&gt;()
    ///     .UseCurlHandler(_ => new CurlHttpClientOptions { EnableHttp2 = true });
    /// </example>
    public static IHttpClientBuilder UseCurlHandler(
        this IHttpClientBuilder builder,
        Func<IServiceProvider, CurlHttpClientOptions>? optionsFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new CurlHttpMessageHandler(
                    optionsFactory?.Invoke(serviceProvider) ?? new CurlHttpClientOptions(),
                    serviceProvider.GetService<ILogger<CurlHttpMessageHandler>>()))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }

    /// <summary>Overload taking a fixed options instance.</summary>
    public static IHttpClientBuilder UseCurlHandler(
        this IHttpClientBuilder builder, CurlHttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseCurlHandler(_ => options);
    }
}
