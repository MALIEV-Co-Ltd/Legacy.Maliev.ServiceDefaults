using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Maliev.Aspire.ServiceDefaults.Caching;

/// <summary>
/// In-memory implementation of ICacheService for fallback when Redis is unavailable.
/// Uses IMemoryCache for storage with pattern-matching support.
/// </summary>
public class InMemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<InMemoryCacheService> _logger;
    private readonly ConcurrentDictionary<string, byte> _keys;
    private readonly object _incrementLock = new object();

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryCacheService"/> class.
    /// </summary>
    /// <param name="cache">The memory cache instance.</param>
    /// <param name="logger">The logger instance.</param>
    public InMemoryCacheService(IMemoryCache cache, ILogger<InMemoryCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
        _keys = new ConcurrentDictionary<string, byte>();
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            if (_cache.TryGetValue<T>(key, out var value))
            {
                return Task.FromResult<T?>(value);
            }

            return Task.FromResult<T?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve from in-memory cache for key: {Key}", key);
            return Task.FromResult<T?>(null);
        }
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1 // Required when SizeLimit is set on MemoryCache
            };

            _cache.Set(key, value, options);
            _keys.TryAdd(key, 0); // Track key for pattern matching

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set in-memory cache for key: {Key}", key);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove in-memory cache key: {Key}", key);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert Redis pattern to regex (simple implementation)
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            var regex = new System.Text.RegularExpressions.Regex(regexPattern);

            var matchingKeys = _keys.Keys.Where(k => regex.IsMatch(k)).ToList();

            foreach (var key in matchingKeys)
            {
                _cache.Remove(key);
                _keys.TryRemove(key, out _);
            }

            _logger.LogInformation("Removed {Count} keys matching pattern {Pattern} from in-memory cache", matchingKeys.Count, pattern);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove keys by pattern {Pattern} from in-memory cache", pattern);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(_cache.TryGetValue(key, out _));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check key existence in in-memory cache: {Key}", key);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            lock (_incrementLock)
            {
                long newValue;
                if (_cache.TryGetValue<long>(key, out var existingValue))
                {
                    // Key exists, increment it
                    newValue = existingValue + 1;
                }
                else
                {
                    // Key doesn't exist, start at 1
                    newValue = 1L;
                    _keys.TryAdd(key, 0);
                }

                // Set the new value with TTL
                _cache.Set(key, newValue, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    Size = 1 // Required when SizeLimit is set on MemoryCache
                });

                return Task.FromResult(newValue);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to increment in-memory cache key: {Key}", key);
            return Task.FromResult(0L);
        }
    }
}
