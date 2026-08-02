using Microsoft.Extensions.Http.Resilience;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the shared HTTP resilience policy used by the legacy services.
/// </summary>
public static class LegacyHttpResilienceExtensions
{
    /// <summary>
    /// Adds the legacy standard resilience pipeline.
    /// </summary>
    /// <remarks>
    /// Retries are intentionally disabled for unsafe HTTP methods. A transient
    /// retry of a legacy write can create a duplicate order, quotation, upload,
    /// or notification when the first request was accepted but its response was
    /// lost. Callers that need a write retry must own idempotency at their API
    /// boundary instead of opting out of this invariant.
    /// </remarks>
    public static IHttpClientBuilder AddLegacyStandardResilienceHandler(
        this IHttpClientBuilder builder,
        Action<HttpStandardResilienceOptions>? customize = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddStandardResilienceHandler(options =>
        {
            ConfigureLegacyStandardResilience(options);
            customize?.Invoke(options);

            // Keep the no-duplicate-write invariant even when a client adjusts
            // timeout, retry-count, or circuit-breaker settings.
            options.Retry.DisableForUnsafeHttpMethods();
        });

        return builder;
    }

    /// <summary>
    /// Applies the common legacy timeout and circuit-breaker defaults.
    /// </summary>
    public static void ConfigureLegacyStandardResilience(HttpStandardResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(65);
        options.Retry.DisableForUnsafeHttpMethods();
    }
}
