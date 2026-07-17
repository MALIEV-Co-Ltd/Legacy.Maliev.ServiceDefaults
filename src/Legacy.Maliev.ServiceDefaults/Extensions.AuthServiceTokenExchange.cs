using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers the opt-in AuthService-backed service-token exchange.
/// </summary>
public static class AuthServiceTokenExchangeExtensions
{
    private const string IamServiceClientName = "IAMService";

    /// <summary>
    /// Adds the opt-in AuthService token handler to a downstream HTTP client.
    /// </summary>
    /// <param name="builder">The downstream HTTP client builder.</param>
    /// <returns>The downstream HTTP client builder.</returns>
    public static IHttpClientBuilder AddAuthServiceAuthentication(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHttpMessageHandler<AuthServiceTokenExchangeHandler>();
    }

    /// <summary>
    /// Adds the IAM permission client using an AuthService-issued workload token without enabling legacy local signing.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The application builder.</returns>
    public static IHostApplicationBuilder AddAuthServiceIAMClient(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IamClientRegistrationGuard.EnsureAuthServiceClientCanRegister(builder.Services);
        if (!IamClientRegistrationGuard.IsAuthServiceTokenExchangeRegistered(builder.Services))
        {
            throw new InvalidOperationException(
                "AddAuthServiceTokenExchange must be registered before the AuthService-backed IAM client.");
        }

        if (!TryResolveIamServiceBaseAddress(
                builder.Configuration,
                builder.Environment.EnvironmentName,
                out var baseAddress))
        {
            throw new InvalidOperationException(
                "Services:IAMService:BaseUrl must be a canonical HTTPS origin. Plain HTTP is allowed only for a loopback origin in Development or Testing.");
        }

        if (!IamClientRegistrationGuard.TryReserveAuthServiceClient(builder.Services))
        {
            return builder;
        }

        builder.Services.AddScoped<IIamServiceClient, IamServiceClient>();
        var iamClientBuilder = builder.Services.AddHttpClient(IamServiceClientName, client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .RedactLoggedHeaders(["X-Maliev-IAM-Live-Check-Key"]);

        if (builder.Configuration["Services:IAMService:BaseUrl"] is null)
        {
            iamClientBuilder.AddServiceDiscovery();
        }

        iamClientBuilder.AddAuthServiceAuthentication();

        return builder;
    }

    /// <summary>
    /// Adds the AuthService token provider and outbound handler without changing legacy service-account clients.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="processServiceName">The canonical code-owned process identity expected in issued tokens.</param>
    /// <returns>The application builder.</returns>
    public static IHostApplicationBuilder AddAuthServiceTokenExchange(
        this IHostApplicationBuilder builder,
        string processServiceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(processServiceName) ||
            processServiceName.Length > 128 ||
            !string.Equals(processServiceName, processServiceName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A bounded canonical process service name is required.", nameof(processServiceName));
        }

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ServiceProcessIdentity)))
        {
            throw new InvalidOperationException("AuthService token exchange can be registered only once per process.");
        }

        IamClientRegistrationGuard.MarkAuthServiceTokenExchangeRegistered(builder.Services);

        builder.Services.AddOptions<AuthServiceTokenExchangeOptions>()
            .Bind(builder.Configuration.GetSection(AuthServiceTokenExchangeOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                _ => AuthServiceTokenProvider.HasValidTrustConfiguration(builder.Configuration),
                "Jwt:PublicKey must be an RSA SPKI key of at least 2048 bits, and Jwt:Issuer and Jwt:Audience must be absolute HTTPS identifiers.")
            .Validate(
                options => TryResolveAuthServiceBaseAddress(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    out _),
                "Services:AuthService:BaseUrl must be a canonical HTTPS origin. Plain HTTP is allowed only for a loopback origin in Development or Testing.")
            .ValidateOnStart();

        builder.Services.AddSingleton(new ServiceProcessIdentity(processServiceName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAuthServiceTokenProvider, AuthServiceTokenProvider>();
        builder.Services.AddTransient<AuthServiceTokenExchangeHandler>();

        builder.Services.AddHttpClient(AuthServiceTokenProvider.HttpClientName, client =>
        {
            if (!TryResolveAuthServiceBaseAddress(
                    builder.Configuration,
                    builder.Environment.EnvironmentName,
                    out var baseAddress))
            {
                throw new InvalidOperationException(
                    "Services:AuthService:BaseUrl is not safe for service credential exchange.");
            }

            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddServiceDiscovery();

        return builder;
    }

    private static bool TryResolveAuthServiceBaseAddress(
        IConfiguration configuration,
        string environmentName,
        out Uri baseAddress)
    {
        var configuredValue = configuration["Services:AuthService:BaseUrl"];
        if (configuredValue is null)
        {
            baseAddress = new Uri(IsLocalEnvironment(environmentName)
                ? "https+http://AuthService"
                : "https://AuthService");
            return true;
        }

        baseAddress = null!;
        if (string.IsNullOrWhiteSpace(configuredValue) ||
            !string.Equals(configuredValue, configuredValue.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(configuredValue, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        var canonicalOrigin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (!string.Equals(configuredValue, canonicalOrigin, StringComparison.Ordinal) ||
            !(string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
              string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
              IsLocalEnvironment(environmentName) &&
              uri.IsLoopback))
        {
            return false;
        }

        baseAddress = uri;
        return true;
    }

    private static bool TryResolveIamServiceBaseAddress(
        IConfiguration configuration,
        string environmentName,
        out Uri baseAddress)
    {
        var configuredValue = configuration["Services:IAMService:BaseUrl"];
        if (configuredValue is null)
        {
            baseAddress = new Uri(IsLocalEnvironment(environmentName)
                ? "https+http://IAMService"
                : "https://IAMService");
            return true;
        }

        baseAddress = null!;
        if (string.IsNullOrWhiteSpace(configuredValue) ||
            !string.Equals(configuredValue, configuredValue.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(configuredValue, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            return false;
        }

        var canonicalOrigin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (!string.Equals(configuredValue, canonicalOrigin, StringComparison.Ordinal) ||
            !(string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
              string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
              IsLocalEnvironment(environmentName) &&
              uri.IsLoopback))
        {
            return false;
        }

        baseAddress = uri;
        return true;
    }

    private static bool IsLocalEnvironment(string environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
}
