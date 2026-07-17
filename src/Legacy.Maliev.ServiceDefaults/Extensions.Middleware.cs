using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding and configuring standard middleware in the application pipeline.
/// </summary>
public static class MiddlewareExtensions
{
    /// <summary>
    /// Adds standard middleware services to the application.
    /// Call this in Program.cs before building the app.
    /// </summary>
    public static IHostApplicationBuilder AddStandardMiddleware(
        this IHostApplicationBuilder builder,
        Action<MiddlewareOptions>? configure = null)
    {
        var options = new MiddlewareOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);

        // Configure Forwarded Headers for microservices architecture
        builder.Services.Configure<ForwardedHeadersOptions>(fhOptions =>
        {
            fhOptions.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Clear known networks and proxies to trust the immediate one (typical for K8s/Docker environments)
            fhOptions.KnownIPNetworks.Clear();
            fhOptions.KnownProxies.Clear();
        });

        return builder;
    }

    /// <summary>
    /// Uses standard middleware in the application pipeline.
    /// Call this in Program.cs after app.Build() and before other middleware.
    /// Order: ForwardedHeaders → CorrelationId → SecurityHeaders → ExceptionHandling → RequestLogging
    /// </summary>
    public static IApplicationBuilder UseStandardMiddleware(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<MiddlewareOptions>() ?? new MiddlewareOptions();

        // Order matters! 
        // ForwardedHeaders must be first to ensure other middleware sees the correct IP/Protocol
        app.UseForwardedHeaders();

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        if (options.EnableRequestLogging)
        {
            app.UseMiddleware<RequestLoggingMiddleware>();
        }

        app.UseMiddleware<ExceptionHandlingMiddleware>();

        return app;
    }
}
/// <summary>
/// Configuration options for standard middleware components.
/// </summary>
public class MiddlewareOptions
{
    /// <summary>
    /// Enable request/response logging (can be verbose).
    /// Default: false
    /// </summary>
    public bool EnableRequestLogging { get; set; } = false;

    /// <summary>
    /// Include stack traces in error responses in development.
    /// Default: true
    /// </summary>
    public bool IncludeStackTraceInDevelopment { get; set; } = true;

    /// <summary>
    /// Correlation ID header name.
    /// Default: X-Correlation-ID
    /// </summary>
    public string CorrelationIdHeaderName { get; set; } = "X-Correlation-ID";

    /// <summary>
    /// Custom security headers to add/override.
    /// </summary>
    public Dictionary<string, string> CustomSecurityHeaders { get; set; } = new();
}
