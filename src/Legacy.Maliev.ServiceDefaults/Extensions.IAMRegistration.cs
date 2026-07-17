using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for IAM registration via RabbitMQ with health check integration.
/// </summary>
public static class IAMRegistrationExtensions
{
    /// <summary>
    /// Adds IAM registration with background RabbitMQ publishing and health check integration.
    /// Registration publishes a message to RabbitMQ for the IAM service to consume.
    /// This is non-blocking and allows services to start immediately.
    /// IMPORTANT: Call this AFTER AddMassTransitWithRabbitMq() to ensure the singleton IBus is available.
    /// </summary>
    /// <typeparam name="TService">The service-specific IAM registration implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceName">The service name used for IAM registration (e.g., "customer", "order").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIAMRegistration<TService>(
        this IServiceCollection services,
        string serviceName)
        where TService : IAMRegistrationService
    {
        // Register the service-specific registration implementation
        services.AddSingleton<TService>();
        services.AddSingleton<IAMRegistrationService>(sp => sp.GetRequiredService<TService>());

        // Register status tracker for health checks (only once)
        services.TryAddSingleton<IAMRegistrationStatusTracker>();

        // Register background service that publishes to RabbitMQ (only once)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BackgroundIAMRegistrationService>());

        // Add health check (only once)
        services.AddHealthChecks()
            .AddCheck<IAMRegistrationHealthCheck>("iam_registration", tags: new[] { "ready" });

        return services;
    }
}
