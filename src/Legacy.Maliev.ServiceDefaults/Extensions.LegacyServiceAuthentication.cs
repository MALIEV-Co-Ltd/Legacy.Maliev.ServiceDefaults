using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers compatibility authentication for independently deployed legacy services.</summary>
public static class LegacyServiceAuthenticationExtensions
{
    /// <summary>Adds the legacy workload-token handler to an explicitly selected HTTP client.</summary>
    public static IHttpClientBuilder AddLegacyServiceAuthentication(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHttpMessageHandler<LegacyServiceAuthenticationHandler>();
    }

    /// <summary>Adds only the opt-in, separately enrolled IAM profile to the selected HTTP client.</summary>
    /// <remarks>Register AddLegacyAuthServiceTokenExchange on the host first to bind credentials and the unauthenticated Auth exchange client.</remarks>
    public static IHttpClientBuilder AddLegacyIamServiceAuthentication(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ILegacyIamServiceAccessTokenProvider, LegacyIamServiceAccessTokenProvider>();
        builder.Services.TryAddTransient<LegacyIamServiceAuthenticationHandler>();
        return builder.AddHttpMessageHandler<LegacyIamServiceAuthenticationHandler>();
    }

    /// <summary>Registers the legacy AuthService camel-case service-login exchange.</summary>
    public static IHostApplicationBuilder AddLegacyAuthServiceTokenExchange(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<LegacyServiceAuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(LegacyServiceAuthenticationOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ILegacyServiceAccessTokenProvider, LegacyServiceAccessTokenProvider>();
        builder.Services.TryAddTransient<LegacyServiceAuthenticationHandler>();
        builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName, client =>
        {
            var configured = builder.Configuration["Services:Auth:BaseUrl"] ?? builder.Configuration["Services:Auth"];
            client.BaseAddress = configured is null
                ? new Uri("https+http://legacy-maliev-auth-service")
                : ResolveBaseAddress(configured);
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddServiceDiscovery();
        return builder;
    }

    private static Uri ResolveBaseAddress(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            !string.Equals(configured, configured.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/") ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Services:Auth must be an absolute service origin without credentials, query, or fragment.");
        return uri;
    }
}
