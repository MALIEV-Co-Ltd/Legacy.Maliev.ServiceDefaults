using Maliev.Aspire.ServiceDefaults.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring distributed caching optimized for low-spec nodes.
/// </summary>
public static class CachingExtensions
{
    private const int DefaultRedisSyncTimeoutMs = 10000;
    private const int DefaultRedisAsyncTimeoutMs = 10000;

    private static string AppendRedisTimeouts(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        var hasSyncTimeout = connectionString.Contains("syncTimeout", StringComparison.OrdinalIgnoreCase);
        var hasAsyncTimeout = connectionString.Contains("asyncTimeout", StringComparison.OrdinalIgnoreCase);

        if (hasSyncTimeout && hasAsyncTimeout)
        {
            return connectionString;
        }

        var builder = new System.Text.StringBuilder(connectionString);
        if (!hasSyncTimeout)
        {
            builder.Append($",syncTimeout={DefaultRedisSyncTimeoutMs}");
        }
        if (!hasAsyncTimeout)
        {
            builder.Append($",asyncTimeout={DefaultRedisAsyncTimeoutMs}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds distributed cache optimized for n1-standard-1 nodes (1 vCPU, 3.75GB RAM).
    /// Uses Redis when available with memory limits, falls back to in-memory cache.
    /// Memory limits: 50MB distributed cache, 25MB local memory cache.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="instanceName">Instance name prefix for cache keys.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddStandardCache(
        this IHostApplicationBuilder builder,
        string instanceName)
    {
        var redisEnabled = builder.Configuration.GetValue<bool>("Cache:RedisEnabled", true);
        var redisConnectionString = builder.Configuration.GetConnectionString("redis") ?? string.Empty;

        // Append timeout parameters to prevent timeout exceptions on slower operations
        redisConnectionString = AppendRedisTimeouts(redisConnectionString);

        if (redisEnabled && !string.IsNullOrEmpty(redisConnectionString) &&
            !builder.Environment.IsEnvironment("Testing"))
        {
            try
            {
                // Register Redis connection multiplexer - connect eagerly so we fail fast if Redis is misconfigured
                var connection = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString);
                builder.Services.AddSingleton<IConnectionMultiplexer>(connection);

                // Add Redis distributed cache
                builder.Services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = redisConnectionString;
                    options.InstanceName = instanceName;
                });

                // Register ICacheService with Redis implementation
                builder.Services.AddScoped<ICacheService, RedisCacheService>();

                // Log successful Redis configuration
                builder.Services.AddSingleton<ILogger<IDistributedCache>>(sp =>
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<IDistributedCache>());
                var logger = builder.Services.BuildServiceProvider()
                    .GetRequiredService<ILogger<IDistributedCache>>();
                logger.LogInformation("Redis cache configured: {InstanceName}", instanceName);
            }
            catch (Exception ex)
            {
                ILogger<IDistributedCache>? logger = null;
                try
                {
                    logger = builder.Services.BuildServiceProvider()
                        .GetRequiredService<ILogger<IDistributedCache>>();
                }
                catch { /* logger not available yet */ }
                if (logger != null)
                    logger.LogWarning(ex, "Redis unavailable, using in-memory cache (limited to 50MB)");
                else
                    Console.WriteLine($"[WARNING] Redis unavailable: {ex.Message}. Falling back to in-memory cache.");

                // Fallback to in-memory cache
                builder.Services.AddDistributedMemoryCache(options =>
                {
                    options.SizeLimit = 50 * 1024 * 1024; // 50MB limit for low-spec nodes
                    options.CompactionPercentage = 0.05; // Aggressive compaction at 95% full
                });

                // Register a no-op ICacheService for in-memory fallback
                // Services that depend on ICacheService will get a working implementation
                builder.Services.AddScoped<ICacheService, InMemoryCacheService>();
            }
        }
        else
        {
            // Use in-memory cache when Redis is disabled or unavailable
            builder.Services.AddDistributedMemoryCache(options =>
            {
                options.SizeLimit = 50 * 1024 * 1024; // 50MB limit
                options.CompactionPercentage = 0.05; // Aggressive compaction
            });

            // Register in-memory ICacheService implementation
            builder.Services.AddScoped<ICacheService, InMemoryCacheService>();
        }

        // Local memory cache with size limits
        builder.Services.AddMemoryCache(options =>
        {
            options.SizeLimit = 25 * 1024 * 1024; // 25MB local cache
            options.CompactionPercentage = 0.10; // Aggressive compaction at 90% full
            options.ExpirationScanFrequency = TimeSpan.FromMinutes(1); // Check for expired items every minute
        });

        return builder;
    }

    /// <summary>
    /// Adds Redis distributed cache with connection string from configuration.
    /// Optimized for low-spec nodes with memory constraints.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="instanceName">Instance name prefix for cache keys.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddRedisDistributedCache(
        this IHostApplicationBuilder builder,
        string instanceName)
    {
        var connectionString = builder.Configuration.GetConnectionString("redis") ?? string.Empty;

        // Append timeout parameters to prevent timeout exceptions on slower operations
        connectionString = AppendRedisTimeouts(connectionString);

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Redis connection string not configured. Set ConnectionStrings:redis in configuration.");
        }

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = connectionString;
            options.InstanceName = instanceName;
        });

        return builder;
    }
}
