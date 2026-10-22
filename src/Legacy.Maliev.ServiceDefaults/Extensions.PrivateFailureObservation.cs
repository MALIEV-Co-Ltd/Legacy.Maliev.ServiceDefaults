using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>Explicitly selects terminal failure observation for one factory-owned HTTP client.</summary>
public static class PrivateFailureObservationExtensions
{
    /// <summary>Adds one outer observer without changing authentication, resilience or logging providers.</summary>
    /// <param name="builder">The explicitly selected named or typed HTTP client.</param>
    /// <param name="dependency">A bounded code-owned dependency identifier, never a URL or customer value.</param>
    /// <returns>The unchanged client builder.</returns>
    public static IHttpClientBuilder AddPrivateFailureObservation(this IHttpClientBuilder builder, string dependency)
        => SelectObservation(builder, dependency, observeOperation: false);

    /// <summary>Adds generic terminal observation with optional bounded runtime exception provenance.</summary>
    /// <param name="builder">The explicitly selected named or typed HTTP client.</param>
    /// <param name="dependency">A bounded code-owned dependency identifier, never a URL or customer value.</param>
    /// <param name="includePrivateProvenance">Whether to capture only validated CLR type and throw-site metadata.</param>
    /// <returns>The unchanged client builder.</returns>
    /// <remarks>Does not widen the generic failure predicate or pass exceptions to logging providers.</remarks>
    public static IHttpClientBuilder AddPrivateFailureObservation(this IHttpClientBuilder builder, string dependency,
        bool includePrivateProvenance)
        => SelectObservation(builder, dependency, observeOperation: false, includePrivateProvenance: includePrivateProvenance);

    /// <summary>Adds terminal failure observation with a finite resource-and-method operation taxonomy.</summary>
    /// <param name="builder">The explicitly selected named or typed HTTP client.</param>
    /// <param name="dependency">A bounded code-owned dependency identifier, never a URL or customer value.</param>
    /// <returns>The unchanged client builder.</returns>
    /// <remarks>Unknown resources or methods emit UnknownOperation; request transport details are never exported.</remarks>
    public static IHttpClientBuilder AddPrivateFailureOperationObservation(this IHttpClientBuilder builder, string dependency)
        => SelectObservation(builder, dependency, observeOperation: true);

    /// <summary>Adds bounded operation observation for terminal non-success responses and unexpected transport exceptions.</summary>
    /// <param name="builder">The explicitly selected named or typed HTTP client.</param>
    /// <param name="dependency">A bounded code-owned dependency identifier, never a URL or customer value.</param>
    /// <returns>The unchanged client builder.</returns>
    /// <remarks>
    /// Preserves the original outbound helper's failure predicate without exporting its host, path or exception details.
    /// Caller cancellation remains quiet. Existing generic and operation-only registrations retain their behavior.
    /// </remarks>
    public static IHttpClientBuilder AddPrivateFailureSourceObservation(this IHttpClientBuilder builder, string dependency)
        => SelectObservation(builder, dependency, observeOperation: true, observeAllFailures: true);

    private static IHttpClientBuilder SelectObservation(IHttpClientBuilder builder, string dependency, bool observeOperation,
        bool observeAllFailures = false, bool includePrivateProvenance = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(builder.Name) || builder.Name.Length > 256 || builder.Name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("An explicit bounded client name is required.", nameof(builder));
        }
        if (string.IsNullOrEmpty(dependency) || dependency.Length > 96 || !char.IsAsciiLetter(dependency[0])
            || !dependency.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'))
        {
            throw new ArgumentException("A bounded code-owned dependency identifier is required.", nameof(dependency));
        }

        var existing = builder.Services.Where(descriptor => descriptor.ServiceType == typeof(PrivateFailureClientSelection))
            .Select(descriptor => descriptor.ImplementationInstance).OfType<PrivateFailureClientSelection>()
            .FirstOrDefault(selection => string.Equals(selection.ClientName, builder.Name, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!string.Equals(existing.Dependency, dependency, StringComparison.Ordinal)
                || existing.ObserveOperation != observeOperation || existing.ObserveAllFailures != observeAllFailures
                || existing.IncludePrivateProvenance != includePrivateProvenance)
            {
                throw new InvalidOperationException("The selected client already has a different failure observation identity or mode.");
            }
            return builder;
        }

        builder.Services.AddSingleton(new PrivateFailureClientSelection(builder.Name, dependency, observeOperation, observeAllFailures, includePrivateProvenance));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, PrivateFailureHandlerBuilderFilter>());
        return builder;
    }

    private sealed record PrivateFailureClientSelection(string ClientName, string Dependency, bool ObserveOperation, bool ObserveAllFailures,
        bool IncludePrivateProvenance);

    private sealed class PrivateFailureHandlerBuilderFilter(IEnumerable<PrivateFailureClientSelection> selections)
        : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            var selection = selections.FirstOrDefault(value => string.Equals(value.ClientName, builder.Name, StringComparison.Ordinal));
            if (selection is not null)
            {
                builder.AdditionalHandlers.Insert(0, new PrivateDependencyFailureHandler(
                    builder.Services.GetRequiredService<ILogger<PrivateDependencyFailureHandler>>(), selection.Dependency,
                    selection.ObserveOperation, selection.ObserveAllFailures, selection.IncludePrivateProvenance));
            }
        };
    }
}
