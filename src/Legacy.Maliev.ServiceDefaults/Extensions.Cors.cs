using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring CORS policies in the application.
/// </summary>
public static class CorsExtensions
{
    /// <summary>
    /// Adds CORS with default policy reading from configuration.
    /// Reads allowed origins from "CORS:AllowedOrigins" configuration (comma-separated).
    /// Falls back to localhost:3000 if not configured.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="policyName">Optional custom policy name (defaults to default policy).</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddDefaultCors(
        this IHostApplicationBuilder builder,
        string? policyName = null)
    {
        var corsOrigins = builder.Configuration["CORS:AllowedOrigins"]?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            ?? new[] { "http://localhost:3000" };

        builder.Services.AddCors(options =>
        {
            options.AddPolicy(policyName ?? "__DefaultCorsPolicy", policy =>
            {
                policy.WithOrigins(corsOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            });

            // Also set as default policy if no custom name provided
            if (policyName == null)
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.WithOrigins(corsOrigins)
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                });
            }
        });

        return builder;
    }

    /// <summary>
    /// Adds CORS with fail-fast validation for production environments.
    /// Requires CORS:AllowedOrigins to be explicitly configured.
    /// Throws InvalidOperationException if not configured in non-development environments.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The configured builder.</returns>
    /// <exception cref="InvalidOperationException">Thrown when CORS origins are not configured in non-development environments.</exception>
    public static IHostApplicationBuilder AddStandardCors(
        this IHostApplicationBuilder builder)
    {
        // Try JSON array config first (e.g. appsettings.json with array values),
        // then fall back to a single comma-separated string value.
        // The single-string path handles Aspire's env var injection which maps
        // CORS__AllowedOrigins=<comma-separated> to the config key "CORS:AllowedOrigins".
        var corsOrigins = builder.Configuration
                              .GetSection("CORS:AllowedOrigins")
                              .Get<string[]>()
                          ?? builder.Configuration["CORS:AllowedOrigins"]
                                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          ?? builder.Configuration["CORS__AllowedOrigins"]
                                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          ?? builder.Configuration["CORS_ALLOWED_ORIGINS"]
                                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          ?? Array.Empty<string>();

        if (corsOrigins.Length == 0)
        {
            if (builder.Environment.IsDevelopment())
            {
                // In development, default to localhost:3000 but log warning
                corsOrigins = new[] { "http://localhost:3000" };

                using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
                var logger = loggerFactory.CreateLogger("CorsExtensions");
                logger.LogWarning("CORS origins not configured. Using default: http://localhost:3000");
            }
            else
            {
                var message = "CORS origins not configured. Set CORS:AllowedOrigins in configuration. " +
                    "This is a FATAL error in production.";

                using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
                var logger = loggerFactory.CreateLogger("CorsExtensions");
                logger.LogCritical("FATAL: {Message}", message);

                throw new InvalidOperationException(message);
            }
        }

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(corsOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            });
        });

        return builder;
    }

    /// <summary>
    /// Adds CORS with custom configuration.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Action to configure CORS options.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddCorsWithOptions(
        this IHostApplicationBuilder builder,
        Action<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions> configure)
    {
        builder.Services.AddCors(configure);
        return builder;
    }
}
