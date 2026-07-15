using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CurlHttp.DependencyInjection;

public static class CurlHttpClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named <see cref="HttpClient"/> whose primary handler is a
    /// <see cref="CurlHttpMessageHandler"/> (libcurl + OpenSSL transport).
    ///
    /// The handler lifetime is set to infinite: the handler is designed to be
    /// long-lived (its native connection pools live inside it) and connection
    /// staleness is already bounded by
    /// <see cref="CurlHttpClientOptions.PooledConnectionLifetime"/>. The
    /// factory's default 2-minute rotation would discard native pools for no
    /// benefit.
    /// </summary>
    /// <example>
    /// services.AddCurlHttpClient("ModernTlsClient", _ => new CurlHttpClientOptions
    /// {
    ///     ConnectTimeout = TimeSpan.FromSeconds(15),
    ///     AllowAutoRedirect = true,
    /// });
    /// </example>
    public static IHttpClientBuilder AddCurlHttpClient(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, CurlHttpClientOptions>? optionsFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new CurlHttpMessageHandler(
                    optionsFactory?.Invoke(serviceProvider) ?? new CurlHttpClientOptions(),
                    serviceProvider.GetService<ILogger<CurlHttpMessageHandler>>()))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }

    /// <summary>Overload taking a fixed options instance.</summary>
    public static IHttpClientBuilder AddCurlHttpClient(
        this IServiceCollection services,
        string name,
        CurlHttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddCurlHttpClient(name, _ => options);
    }
}
