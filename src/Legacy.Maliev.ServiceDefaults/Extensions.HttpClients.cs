using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

// Standardized HTTP client extensions for Maliev microservices.
/// <summary>
/// Provides extension methods for registering and configuring HTTP clients for MALIEV microservices
/// with standardized service discovery, resilience patterns, and service account authentication.
/// </summary>
public static class HttpClientExtensions
{
    /// <summary>
    /// Adds IAM service HTTP client with standard resilience and service account authentication.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">Optional explicit service name. If null, reads from "ServiceName" config.</param>
    public static IHostApplicationBuilder AddIAMServiceClient(
        this IHostApplicationBuilder builder,
        string? serviceName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IamClientRegistrationGuard.EnsureLegacyClientCanRegister(builder.Services);

        // Require explicit name via parameter or configuration
        // REMOVED multiple fallbacks to ensure standardized configuration
        var finalServiceName = serviceName
            ?? builder.Configuration["ServiceName"]
            ?? throw new InvalidOperationException("Service name must be provided to AddIAMServiceClient via the 'serviceName' parameter or configuration key 'ServiceName'.");

        if (!IamClientRegistrationGuard.TryReserveLegacyClient(builder.Services))
        {
            return builder;
        }

        // Register the named client "IAMService" with full configuration (resilience + service account auth)
        // This uses the AddIAMClient extension which configures auth handler and service discovery
        builder.Services.AddIAMClient(builder.Configuration, finalServiceName);

        // Register the IamServiceClient which uses IHttpClientFactory to create "IAMService" clients
        builder.Services.AddScoped<IIamServiceClient, IamServiceClient>();

        return builder;
    }

    /// <summary>
    /// Adds Upload service HTTP client with standard resilience.
    /// </summary>
    public static IHostApplicationBuilder AddUploadServiceClient(
        this IHostApplicationBuilder builder)
    {
        builder.AddServiceClient("UploadService", configureClient: client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            return new HttpClientHandler
            {
                MaxRequestContentBufferSize = 100 * 1024 * 1024, // 100MB for large files
                AllowAutoRedirect = true
            };
        });

        return builder;
    }

    /// <summary>
    /// Adds PDF service HTTP client with standard resilience.
    /// </summary>
    public static IHostApplicationBuilder AddPdfServiceClient(
        this IHostApplicationBuilder builder)
    {
        builder.AddServiceClient("PdfService", configureClient: client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        return builder;
    }

    /// <summary>
    /// Adds Notification service HTTP client with standard resilience.
    /// </summary>
    public static IHostApplicationBuilder AddNotificationServiceClient(
        this IHostApplicationBuilder builder)
    {
        builder.AddServiceClient("NotificationService");
        return builder;
    }

    /// <summary>
    /// Adds Customer service HTTP client with standard resilience.
    /// </summary>
    public static IHostApplicationBuilder AddCustomerServiceClient(
        this IHostApplicationBuilder builder)
    {
        builder.AddServiceClient("CustomerService");
        return builder;
    }

    /// <summary>
    /// Adds a typed service HTTP client with standardized discovery, resilience and Service Account authentication.
    /// </summary>
    /// <typeparam name="TInterface">The HTTP client interface type.</typeparam>
    /// <typeparam name="TImplementation">The HTTP client implementation type.</typeparam>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <param name="sourceServiceName">Optional source service name for authentication (defaults to configured ServiceName).</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddAuthenticatedServiceClient<TInterface, TImplementation>(
        this IHostApplicationBuilder builder,
        string serviceName,
        string? sourceServiceName = null)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        // Ensure ServiceAccountAuthenticationHandler is registered (idempotent - TryAdd)
        builder.Services.TryAddTransient<ServiceAccountAuthenticationHandler>();

        // Register the typed client using the named configuration
        // Note: We do NOT call AddIAMClient here because that would overwrite the "IAMService"
        // named client registration if AddIAMServiceClient was already called.
        // The IAMService named client (base address, auth handler chain) is shared across all
        // authenticated service clients and is only registered once via AddIAMServiceClient.
        return builder.Services.AddHttpClient<TInterface, TImplementation>(serviceName, (sp, client) =>
        {
            // Check if there's an explicit URL configured (for GKE deployment)
            var explicitUrl = builder.Configuration[$"Services:{serviceName}:BaseUrl"];

            if (!string.IsNullOrEmpty(explicitUrl))
            {
                // Use explicit URL for GKE/production
                client.BaseAddress = new Uri(explicitUrl);
            }
            else
            {
                // Prefer HTTPS service-discovery endpoints so authenticated requests do not
                // lose Authorization headers on an HTTP -> HTTPS redirect.
                client.BaseAddress = new Uri($"https+http://{serviceName}");
            }

            client.Timeout = TimeSpan.FromSeconds(90);
        })
        .AddServiceDiscovery() // Resolves serviceName -> BaseAddress via Aspire
        .AddHttpMessageHandler<ServiceAccountAuthenticationHandler>();
    }

    /// <summary>
    /// Adds a typed service HTTP client with standardized discovery and resilience using .NET Aspire service discovery.
    /// </summary>
    /// <typeparam name="TInterface">The HTTP client interface type.</typeparam>
    /// <typeparam name="TImplementation">The HTTP client implementation type.</typeparam>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddServiceClient<TInterface, TImplementation>(
        this IHostApplicationBuilder builder,
        string serviceName)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        return builder.Services.AddServiceClient<TInterface, TImplementation>(builder.Configuration, serviceName);
    }

    /// <summary>
    /// Adds a typed service HTTP client with ENFORCED configuration pattern.
    /// REQUIRED: Services:{ServiceName}:BaseUrl must be configured (no fallbacks).
    /// </summary>
    /// <typeparam name="TInterface">The HTTP client interface type.</typeparam>
    /// <typeparam name="TImplementation">The HTTP client implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddServiceClient<TInterface, TImplementation>(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        var httpClientBuilder = services.AddHttpClient<TInterface, TImplementation>((sp, client) =>
        {
            // Check if there's an explicit URL configured (for GKE deployment)
            var explicitUrl = configuration[$"Services:{serviceName}:BaseUrl"];

            if (!string.IsNullOrEmpty(explicitUrl))
            {
                // Use explicit URL for GKE/production
                client.BaseAddress = new Uri(explicitUrl);
            }
            else
            {
                // Prefer HTTPS service-discovery endpoints so clients do not
                // lose Authorization headers on an HTTP -> HTTPS redirect.
                client.BaseAddress = new Uri($"https+http://{serviceName}");
            }

            client.Timeout = TimeSpan.FromSeconds(90);
        })
        .AddServiceDiscovery(); // Resolves serviceName -> BaseAddress via Aspire

        return httpClientBuilder;
    }

    /// <summary>
    /// Adds a generic named HTTP client with standardized discovery and resilience using .NET Aspire.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <param name="baseUrl">Optional base URL override (for testing or direct URLs).</param>
    /// <param name="configureClient">Optional action to configure the HTTP client.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddServiceClient(
        this IHostApplicationBuilder builder,
        string serviceName,
        string? baseUrl = null,
        Action<HttpClient>? configureClient = null)
    {
        return builder.Services.AddServiceClient(builder.Configuration, serviceName, baseUrl, configureClient);
    }

    /// <summary>
    /// Adds a generic named HTTP client with ENFORCED configuration pattern.
    /// REQUIRED: Services:{ServiceName}:BaseUrl must be configured (no fallbacks).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <param name="baseUrl">Optional base URL override.</param>
    /// <param name="configureClient">Optional action to configure the HTTP client.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddServiceClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        string? baseUrl = null,
        Action<HttpClient>? configureClient = null)
    {
        var httpClientBuilder = services.AddHttpClient(serviceName, (sp, client) =>
        {
            // Check if there's an explicit URL configured (for GKE deployment)
            var explicitUrl = baseUrl ?? configuration[$"Services:{serviceName}:BaseUrl"];

            if (!string.IsNullOrEmpty(explicitUrl))
            {
                // Use explicit URL for GKE/production
                client.BaseAddress = new Uri(explicitUrl);
            }
            else
            {
                // Prefer HTTPS service-discovery endpoints so clients do not
                // lose Authorization headers on an HTTP -> HTTPS redirect.
                client.BaseAddress = new Uri($"https+http://{serviceName}");
            }

            client.Timeout = TimeSpan.FromSeconds(90);

            configureClient?.Invoke(client);
        })
        .AddServiceDiscovery(); // Resolves serviceName -> BaseAddress via Aspire

        return httpClientBuilder;
    }
}
