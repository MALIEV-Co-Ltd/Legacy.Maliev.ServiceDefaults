using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>Explicitly selects private request observation for a caller-owned service prefix.</summary>
public static class PrivateRequestObservationExtensions
{
    /// <summary>Selects an immutable request boundary without selecting a logging provider.</summary>
    /// <param name="builder">The caller-owned host builder.</param>
    /// <param name="servicePrefix">The code-owned prefix of the host's registered health endpoints.</param>
    /// <returns>The unchanged builder.</returns>
    public static IHostApplicationBuilder AddPrivateRequestObservation(
        this IHostApplicationBuilder builder, string servicePrefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(servicePrefix) || servicePrefix.Length > 64
            || !servicePrefix.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("A bounded single-segment service prefix is required.", nameof(servicePrefix));
        }

        var selected = builder.Services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(PrivateRequestObservationSelection))?.ImplementationInstance
            as PrivateRequestObservationSelection;
        if (selected is not null)
        {
            if (!string.Equals(selected.ServicePrefix, servicePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Private request observation is already selected for another prefix.");
            }
            return builder;
        }

        var selection = new PrivateRequestObservationSelection(servicePrefix);
        builder.Services.AddSingleton(selection);
        builder.Services.AddSingleton(services => new PrivateRequestObservationState(selection,
            services.GetService<TimeProvider>() ?? TimeProvider.System));
        return builder;
    }
}
