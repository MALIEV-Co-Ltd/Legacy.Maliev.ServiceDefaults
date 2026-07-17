using Maliev.Aspire.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring rate limiting optimized for low-spec nodes.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Adds standard rate limiting configuration. 
    /// </summary>
    public static IHostApplicationBuilder AddStandardRateLimiting(
        this IHostApplicationBuilder builder,
        Action<RateLimiterOptions>? configure = null)
    {
        builder.Services.Configure<RateLimiterOptions>(builder.Configuration.GetSection("RateLimiting"));
        if (configure != null)
        {
            builder.Services.PostConfigure(configure);
        }

        // Register the dynamic configuration handler
        builder.Services.AddTransient<IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>, ConfigureRateLimiterOptions>();

        builder.Services.AddRateLimiter(_ => { });

        return builder;
    }

    private class ConfigureRateLimiterOptions : IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>
    {
        private readonly IConfiguration _config;
        private readonly RateLimiterOptions _options;

        public ConfigureRateLimiterOptions(IConfiguration config, IOptions<RateLimiterOptions> options)
        {
            _config = config;
            _options = options.Value;
        }

        public void Configure(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions rateLimiterOptions)
        {
            rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Read from current IConfiguration (picks up test overrides)
            var basePermitLimit = _config.GetValue("RateLimiting:PermitLimit", _options.PermitLimit);
            var baseWindowMinutes = _config.GetValue("RateLimiting:WindowMinutes", _options.WindowMinutes);
            var baseQueueLimit = _config.GetValue("RateLimiting:QueueLimit", _options.QueueLimit);
            var useGlobal = _config.GetValue("RateLimiting:UseGlobalLimiter", _options.UseGlobalLimiter);

            var window = TimeSpan.FromMinutes(baseWindowMinutes);

            if (useGlobal)
            {
                rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var partitionKey = context.User.Identity?.Name ??
                                       context.Request.Headers.Host.ToString();

                    return RateLimitPartition.GetSlidingWindowLimiter(
                        partitionKey: partitionKey,
                        factory: _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = basePermitLimit,
                            Window = window,
                            SegmentsPerWindow = 4,
                            QueueProcessingOrder = _options.QueueProcessingOrder,
                            QueueLimit = baseQueueLimit
                        });
                });
            }

            // Standard Policies
            rateLimiterOptions.AddSlidingWindowLimiter("default", lo =>
            {
                lo.PermitLimit = basePermitLimit;
                lo.Window = window;
                lo.SegmentsPerWindow = 4;
                lo.QueueProcessingOrder = _options.QueueProcessingOrder;
                lo.QueueLimit = baseQueueLimit;
            });

            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Api, lo =>
            {
                lo.PermitLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Api}:PermitLimit", basePermitLimit);
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            var publicLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Public}:PermitLimit", basePermitLimit / 2);
            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Public, lo =>
            {
                lo.PermitLimit = publicLimit;
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            var adminLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Admin}:PermitLimit", Math.Max(1, basePermitLimit / 4));
            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Admin, lo =>
            {
                lo.PermitLimit = adminLimit;
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Batch, lo =>
            {
                lo.PermitLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Batch}:PermitLimit", Math.Max(1, basePermitLimit / 10));
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            var authLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Auth}:PermitLimit", Math.Max(1, basePermitLimit / 20));
            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Auth, lo =>
            {
                lo.PermitLimit = authLimit;
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Read, lo =>
            {
                lo.PermitLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Read}:PermitLimit", basePermitLimit * 2);
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            rateLimiterOptions.AddFixedWindowLimiter(RateLimitPolicies.Write, lo =>
            {
                lo.PermitLimit = _config.GetValue($"RateLimiting:{RateLimitPolicies.Write}:PermitLimit", basePermitLimit / 2);
                lo.Window = window;
                lo.QueueLimit = baseQueueLimit;
            });

            // Legacy Aliases
            rateLimiterOptions.AddFixedWindowLimiter("admin-endpoints", lo => { lo.PermitLimit = adminLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("PublicApi", lo => { lo.PermitLimit = publicLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("AuthenticatedApi", lo => { lo.PermitLimit = basePermitLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("token_limit", lo => { lo.PermitLimit = authLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("ContactPolicy", lo => { lo.PermitLimit = publicLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("GlobalPolicy", lo => { lo.PermitLimit = basePermitLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("general", lo => { lo.PermitLimit = basePermitLimit; lo.Window = window; });
            rateLimiterOptions.AddFixedWindowLimiter("anonymous", lo => { lo.PermitLimit = publicLimit; lo.Window = window; });

            rateLimiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Too many requests",
                    message = "Rate limit exceeded. Please try again later.",
                    retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? retryAfter.ToString()
                        : "60"
                }, cancellationToken);
            };
        }
    }
}

/// <summary>
/// Configuration options for rate limiting behavior.
/// </summary>
public class RateLimiterOptions
{
    /// <summary>
    /// Gets or sets whether to use the global rate limiter instead of per-endpoint policies.
    /// </summary>
    public bool UseGlobalLimiter { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum number of requests permitted per time window.
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Gets or sets the duration of the rate limiting time window in minutes.
    /// </summary>
    public double WindowMinutes { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of requests that can be queued when the limit is reached.
    /// </summary>
    public int QueueLimit { get; set; } = 0;

    /// <summary>
    /// Gets or sets the queue processing order for requests waiting for capacity.
    /// </summary>
    public QueueProcessingOrder QueueProcessingOrder { get; set; } = QueueProcessingOrder.OldestFirst;
}
