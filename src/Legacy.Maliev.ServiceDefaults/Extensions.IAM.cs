using Maliev.Aspire.ServiceDefaults.Authorization;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults;

/// <summary>
/// Extensions for IAM client configuration.
/// </summary>
public static class IAMExtensions
{
    /// <summary>
    /// Adds and configures a resilient IAM client with service account authentication.
    /// </summary>
    public static IHttpClientBuilder AddIAMClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        // Register the token provider so the handler can resolve it
        services.TryAddSingleton<IServiceAccountTokenProvider>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new ServiceAccountTokenProvider(config, serviceName);
        });

        // Register the handler in DI so it can be resolved by the HttpClient builder
        services.TryAddTransient<ServiceAccountAuthenticationHandler>();

        var httpClientBuilder = services.AddHttpClient("IAMService").ConfigureHttpClient((sp, client) =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IAMClientConfig");

            // Check if there's an explicit URL configured (for GKE deployment)
            var explicitUrl = configuration["Services:IAMService:BaseUrl"];

            // DEBUG: Log what we're reading from configuration
            logger.LogDebug("[IAM Client Config] Services:IAMService:BaseUrl = '{ExplicitUrl}'", explicitUrl ?? "null");

            if (!string.IsNullOrEmpty(explicitUrl))
            {
                // Use explicit URL for GKE/production
                logger.LogInformation("[IAM Client Config] Using explicit BaseAddress: {ExplicitUrl}", explicitUrl);
                client.BaseAddress = new Uri(explicitUrl);
            }
            else
            {
                // Prefer HTTPS service-discovery endpoints so service-account tokens are
                // not stripped by an HTTP -> HTTPS redirect.
                logger.LogDebug("[IAM Client Config] Using service discovery with service name: https+http://IAMService");
                client.BaseAddress = new Uri("https+http://IAMService");
            }

            client.DefaultRequestHeaders.Add("X-Service-Name", serviceName);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .RedactLoggedHeaders(["X-Maliev-IAM-Live-Check-Key"]);

        // Add the authentication handler only once
        httpClientBuilder.AddHttpMessageHandler<ServiceAccountAuthenticationHandler>();

        // Enable Aspire service discovery to resolve IAMService endpoint
        httpClientBuilder.AddServiceDiscovery();

        return httpClientBuilder;
    }

    /// <summary>
    /// Adds permission-based authorization infrastructure.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // Register authorization services first
        services.AddAuthorization();

        // CRITICAL: Replace the default policy provider with our custom one
        // Using Replace ensures our provider is used even if a default one was registered
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>());
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
