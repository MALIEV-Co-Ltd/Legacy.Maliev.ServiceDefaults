using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Unified service client registration extensions for batch registration of typed HTTP clients.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Unified service client registration with automatic service discovery.
    /// Supports both Aspire (ConnectionStrings) and GKE (Services:ServiceName:BaseUrl) environments.
    /// </summary>
    /// <typeparam name="TInterface">The interface type for the service client.</typeparam>
    /// <typeparam name="TImplementation">The implementation type for the service client.</typeparam>
    /// <param name="builder">The application builder.</param>
    /// <param name="serviceName">The name of the service to connect to.</param>
    /// <param name="configureClient">Optional action to configure the HttpClient.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds (default: 90).</param>
    /// <returns>The configured <see cref="IHttpClientBuilder"/>.</returns>
    public static IHttpClientBuilder AddServiceClient<TInterface, TImplementation>(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<HttpClient>? configureClient = null,
        int? timeoutSeconds = null)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        var httpClientBuilder = builder.Services.AddHttpClient<TInterface, TImplementation>((sp, client) =>
        {
            var url = builder.Configuration.GetConnectionString(serviceName)
                ?? builder.Configuration[$"Services:{serviceName}:BaseUrl"]
                ?? throw new InvalidOperationException(
                    $"Service '{serviceName}' not configured. " +
                    $"Set ConnectionStrings:{serviceName} (Aspire) or Services:{serviceName}:BaseUrl (GKE)");

            client.BaseAddress = new Uri(url);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds ?? 90);
            configureClient?.Invoke(client);

            var logger = sp.GetRequiredService<ILogger<TImplementation>>();
            var source = builder.Configuration.GetConnectionString(serviceName) != null
                ? "Aspire ConnectionString"
                : "appsettings Services:BaseUrl";
            logger.LogInformation("[OK] {ServiceName} → {Url} (from {Source})",
                serviceName, url, source);
        });

        httpClientBuilder.AddServiceDiscovery();
        httpClientBuilder.AddStandardResilienceHandler();

        return httpClientBuilder;
    }

    /// <summary>
    /// Batch registration for BFF aggregators - replaces 16+ manual registrations.
    /// Provides a fluent API for registering multiple typed service clients at once.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Action to configure service client registrations.</param>
    /// <returns>The configured <see cref="IHostApplicationBuilder"/>.</returns>
    public static IHostApplicationBuilder AddServiceClients(
        this IHostApplicationBuilder builder,
        Action<ServiceClientRegistrar> configure)
    {
        var registrar = new ServiceClientRegistrar(builder);
        configure(registrar);
        return builder;
    }

    /// <summary>
    /// Fluent registrar for batch service client registration.
    /// </summary>
    public class ServiceClientRegistrar
    {
        private readonly IHostApplicationBuilder _builder;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceClientRegistrar"/> class.
        /// </summary>
        /// <param name="builder">The application builder.</param>
        public ServiceClientRegistrar(IHostApplicationBuilder builder) => _builder = builder;

        /// <summary>
        /// Adds a typed service client to the registration batch.
        /// </summary>
        /// <typeparam name="TInterface">The interface type for the service client.</typeparam>
        /// <typeparam name="TImplementation">The implementation type for the service client.</typeparam>
        /// <param name="serviceName">The name of the service to connect to.</param>
        /// <param name="configure">Optional action to configure the HttpClient.</param>
        /// <returns>This <see cref="ServiceClientRegistrar"/> for method chaining.</returns>
        public ServiceClientRegistrar Add<TInterface, TImplementation>(
            string serviceName,
            Action<HttpClient>? configure = null)
            where TInterface : class
            where TImplementation : class, TInterface
        {
            _builder.AddServiceClient<TInterface, TImplementation>(serviceName, configure);
            return this;
        }
    }
}
