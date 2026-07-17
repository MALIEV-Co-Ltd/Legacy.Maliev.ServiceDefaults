using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Maliev.Aspire.ServiceDefaults.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for configuring service defaults in a distributed application.
/// This class includes methods for setting up OpenTelemetry, health checks, service discovery, and resilience.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Adds default service configurations to the application builder.
    /// This includes setting up OpenTelemetry for logging, metrics, and tracing,
    /// configuring health checks for the application and its dependencies,
    /// and enabling service discovery and resilience for HTTP clients.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to configure.</param>
    /// <returns>The configured <see cref="IHostApplicationBuilder"/>.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // Disable GSSAPI authentication globally to avoid SPNEGO negotiation noise in postgres-server logs.
        // This resolves the "DETAIL: No credentials were supplied... SPNEGO cannot find mechanisms to negotiate" error.
        Environment.SetEnvironmentVariable("NPGSQL_GSSAPI_AUTHENTICATION", "false");
        Environment.SetEnvironmentVariable("PGGSSENCMODE", "disable");

        // Disable MassTransit usage telemetry to prevent "Usage Telemetry:" JSON logs
        Environment.SetEnvironmentVariable("MASSTRANSIT_USAGE_TELEMETRY", "false");

        // Reduce log verbosity for noisy categories
        // ASP.NET Core infrastructure (keep errors only)
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.ResponseCaching", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.Infrastructure", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Mvc.ModelBinding", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Antiforgery", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.StaticAssets", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning); // Reduce startup/shutdown noise

        // Health checks - Enabled for debugging
        builder.Logging.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService", LogLevel.Warning);

        // Service discovery and resilience (temporarily verbose for debugging)
        builder.Logging.AddFilter("Microsoft.Extensions.ServiceDiscovery", LogLevel.Information);
        builder.Logging.AddFilter("Polly", LogLevel.Error);

        // Infrastructure components
        builder.Logging.AddFilter("StackExchange.Redis", LogLevel.Warning); // Redis connection noise
        builder.Logging.AddFilter("Npgsql", LogLevel.Warning); // PostgreSQL connection noise
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Critical);

        // MassTransit/RabbitMQ - Warning level for transport; Information for message processing
        builder.Logging.AddFilter("MassTransit", LogLevel.Warning);
        builder.Logging.AddFilter("MassTransit.Messages", LogLevel.Information);

        // IAM and Authorization
        builder.Logging.AddFilter("IAM.Handler.Factory", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authorization", LogLevel.Warning);

        // --- OpenTelemetry ---
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                logging.AddOtlpExporter();
            }
        });

        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var enableTracing = useOtlpExporter ||
            builder.Configuration.GetValue("Observability:TracingEnabled", false);
        var enableRuntimeMetrics = builder.Configuration.GetValue(
            "Observability:RuntimeMetricsEnabled",
            !builder.Environment.IsDevelopment());

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (enableRuntimeMetrics)
                {
                    metrics.AddRuntimeInstrumentation();
                }

                metrics.AddPrometheusExporter(); // Export metrics for Prometheus

                if (useOtlpExporter)
                {
                    metrics.AddOtlpExporter();
                }
            });

        if (enableTracing)
        {
            openTelemetry.WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = IsNotAspireLivenessRequest;
                })
                       .AddHttpClientInstrumentation()
                       .AddSource("MassTransit") // Track messaging activities
                       .AddSource("Npgsql")       // Track DB activities
                       .AddProcessor(new UrlQueryRedactionProcessor());

                if (useOtlpExporter)
                {
                    tracing.AddOtlpExporter();
                }
            });
        }


        // --- Health Checks ---
        var healthChecksBuilder = builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        // Configure HealthCheck publisher options for background checks
        builder.Services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(30); // Increased delay (10s -> 30s) to allow migrations and IAM registration to stabilize
            options.Period = TimeSpan.FromSeconds(30);
            options.Timeout = TimeSpan.FromMinutes(3); // Increased timeout (2m -> 3m) for heavy startup load
        });

        // --- Service Discovery and Resilience ---
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default with optimized timeouts
            http.AddStandardResilienceHandler(options =>
            {
                // Tuned timeouts: IAM registration can be slow but 100s is excessive
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(65); // Must be >= 2 * AttemptTimeout
            });

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    private static bool IsNotAspireLivenessRequest(HttpContext context)
    {
        var path = context.Request.Path.Value;
        return path is null || !path.EndsWith(
            "/aspire-liveness",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds service-specific meters to OpenTelemetry metrics collection.
    /// Call this AFTER AddServiceDefaults() to register custom business metrics meters.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to configure.</param>
    /// <param name="meterNames">Names of meters to add (e.g., "customer-service", "payment-service").</param>
    /// <returns>The configured <see cref="IHostApplicationBuilder"/>.</returns>
    public static IHostApplicationBuilder AddServiceMeters(this IHostApplicationBuilder builder, params string[] meterNames)
    {
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
        {
            foreach (var meterName in meterNames)
            {
                metrics.AddMeter(meterName);
            }
        });

        return builder;
    }

    /// <summary>
    /// Maps default endpoints for health, metrics, and API documentation.
    /// </summary>
    /// <param name="app">The <see cref="WebApplication"/> to configure.</param>
    /// <param name="servicePrefix">Required service prefix for health and metrics endpoints.</param>
    /// <returns>The configured <see cref="WebApplication"/>.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app, string servicePrefix)
    {
        if (string.IsNullOrWhiteSpace(servicePrefix))
        {
            throw new ArgumentException("Service prefix is required for MapDefaultEndpoints", nameof(servicePrefix));
        }

        // Aspire-specific liveness check (minimal, fast)
        // Only checks if service process is running - optimized for Aspire orchestration
        // This endpoint responds in < 10ms to avoid Aspire's hardcoded 3-second timeout
        app.MapGet($"/{servicePrefix}/aspire-liveness", () => "Healthy")
            .WithTags("aspire")
            .AllowAnonymous();

        // Liveness endpoint - simple check that always returns healthy (for Kubernetes ingress)
        app.MapGet($"/{servicePrefix}/liveness", () => "Healthy")
            .WithTags("kubernetes")
            .AllowAnonymous();

        // Readiness endpoint - comprehensive checks for all dependencies (for Kubernetes ingress)
        // Performs thorough validation: Database, Redis, RabbitMQ, IAM registration
        // May take 900-2600ms depending on infrastructure load
        app.MapHealthChecks($"/{servicePrefix}/readiness", new HealthCheckOptions
        {
            Predicate = _ => true, // All health checks must pass
            ResponseWriter = async (context, report) =>
            {
                // Return detailed JSON health check response for monitoring and debugging
                context.Response.ContentType = "application/json";
                var result = System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(
                        e => e.Key,
                        e => new
                        {
                            status = e.Value.Status.ToString(),
                            description = e.Value.Description ?? string.Empty,
                            duration = e.Value.Duration.TotalMilliseconds,
                            exception = e.Value.Exception?.Message ?? string.Empty,
                            data = e.Value.Data
                        }),
                    totalDuration = report.TotalDuration.TotalMilliseconds
                });
                await context.Response.WriteAsync(result);
            }
        })
        .WithTags("kubernetes");

        // OpenTelemetry Prometheus metrics endpoint at /{servicePrefix}/metrics
        app.MapPrometheusScrapingEndpoint($"/{servicePrefix}/metrics");

        return app;
    }
}
