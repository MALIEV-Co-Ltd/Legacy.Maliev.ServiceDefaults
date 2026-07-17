using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace Maliev.Aspire.ServiceDefaults.Caching;

/// <summary>
/// Standardized Redis implementation of ICacheService using IConnectionMultiplexer directly.
/// </summary>
public class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _instanceName;

    /// <summary>
    /// Initializes a new instance of the RedisCacheService with the specified dependencies.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="options">Redis cache configuration options.</param>
    /// <param name="logger">Logger for cache operations.</param>
    public RedisCacheService(
        IConnectionMultiplexer redis,
        IOptions<RedisCacheOptions> options,
        ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _logger = logger;
        _instanceName = options.Value.InstanceName ?? string.Empty;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Retrieves a value from the Redis cache by key.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The cached value if found, otherwise null.</returns>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var database = _redis.GetDatabase();
            var prefixedKey = _instanceName + key;
            var value = await database.StringGetAsync(prefixedKey);

            if (value.IsNull)
            {
                return null;
            }

            // If T is string, return directly to avoid double quotes from JSON serialization
            if (typeof(T) == typeof(string))
            {
                return value.ToString() as T;
            }

            return JsonSerializer.Deserialize<T>(value.ToString()!, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve from cache for key: {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Stores a value in the Redis cache with the specified time-to-live.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="ttl">The time-to-live for the cached entry.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var database = _redis.GetDatabase();
            var prefixedKey = _instanceName + key;

            string json;
            if (value is string s)
            {
                json = s;
            }
            else
            {
                json = JsonSerializer.Serialize(value, _jsonOptions);
            }

            await database.StringSetAsync(prefixedKey, json, ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set cache for key: {Key}", key);
        }
    }

    /// <summary>
    /// Removes a value from the Redis cache by key.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _redis.GetDatabase();
            var prefixedKey = _instanceName + key;
            await database.KeyDeleteAsync(prefixedKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache key: {Key}", key);
        }
    }

    /// <summary>
    /// Removes all cache entries matching the specified pattern from all Redis endpoints.
    /// Uses SCAN to avoid blocking the Redis server.
    /// </summary>
    /// <param name="pattern">The key pattern to match (supports glob-style patterns).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogWarning("Redis is not connected. Cannot remove by pattern: {Pattern}", pattern);
            return;
        }

        try
        {
            var endpoints = _redis.GetEndPoints();
            var database = _redis.GetDatabase();
            var prefixedPattern = _instanceName + pattern;

            // Use KeysAsync which uses SCAN to avoid blocking the server
            foreach (var endpoint in endpoints)
            {
                var server = _redis.GetServer(endpoint);

                await foreach (var key in server.KeysAsync(pattern: prefixedPattern))
                {
                    await database.KeyDeleteAsync(key);
                }

                _logger.LogInformation("Finished removing keys matching pattern {Pattern} from Redis endpoint {Endpoint}", prefixedPattern, endpoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove keys by pattern {Pattern} from Redis", pattern);
        }
    }

    /// <summary>
    /// Checks whether a key exists in the Redis cache.
    /// </summary>
    /// <param name="key">The cache key to check.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the key exists, otherwise false.</returns>
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _redis.GetDatabase();
            var prefixedKey = _instanceName + key;
            return await database.KeyExistsAsync(prefixedKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check key existence in Redis: {Key}", key);
            return false;
        }
    }

    /// <summary>
    /// Atomically increments a counter in Redis and sets the time-to-live.
    /// Uses a Lua script to ensure atomicity of the increment and expire operations.
    /// </summary>
    /// <param name="key">The cache key for the counter.</param>
    /// <param name="ttl">The time-to-live for the key.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The new value of the counter.</returns>
    public async Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = _redis.GetDatabase();
            var prefixedKey = _instanceName + key;

            // Use Lua script to ensure atomicity of INCR and EXPIRE
            // Only set EXPIRE if the result of INCR is 1 (new key)
            var script = @"
                local newValue = redis.call('INCR', KEYS[1])
                if newValue == 1 then
                    redis.call('EXPIRE', KEYS[1], ARGV[1])
                end
                return newValue";

            var result = await database.ScriptEvaluateAsync(
                script,
                [prefixedKey],
                [(int)ttl.TotalSeconds]);

            return (long)result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to increment cache key: {Key}", key);
            return 0;
        }
    }
}
