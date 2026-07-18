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

    /// <summary>Registers the legacy AuthService camel-case service-login exchange.</summary>
    public static IHostApplicationBuilder AddLegacyAuthServiceTokenExchange(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ILegacyServiceAccessTokenProvider)))
            return builder;

        builder.Services.AddOptions<LegacyServiceAuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(LegacyServiceAuthenticationOptions.SectionName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ILegacyServiceAccessTokenProvider, LegacyServiceAccessTokenProvider>();
        builder.Services.AddTransient<LegacyServiceAuthenticationHandler>();
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
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Services:Auth must be an absolute service origin without credentials, query, or fragment.");
        return uri;
    }
}
