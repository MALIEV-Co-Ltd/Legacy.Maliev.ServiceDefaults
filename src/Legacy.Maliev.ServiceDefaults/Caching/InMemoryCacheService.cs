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
    private readonly ConcurrentDictionary<string, CacheKeyRegistration> _keys;
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
        _keys = new ConcurrentDictionary<string, CacheKeyRegistration>();
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T?>(cancellationToken);
        }

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
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        CacheKeyRegistration? registration = null;
        try
        {
            registration = new CacheKeyRegistration(this, key);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1 // One explicit entry unit for the bounded local cache.
            }.RegisterPostEvictionCallback(static (_, _, _, state) =>
            {
                if (state is CacheKeyRegistration evicted
                    && evicted.Owner.TryGetTarget(out var owner))
                {
                    owner.RemoveRegistration(evicted);
                }
            }, registration);

            // Register before Set so an immediately expired or compacted entry cannot
            // leave a key behind. The registration token prevents an old eviction
            // callback from removing a newer value for the same key.
            _keys[key] = registration;
            _cache.Set(key, value, options);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            if (registration is not null)
            {
                RemoveRegistration(registration);
            }

            _logger.LogWarning(ex, "Failed to set in-memory cache for key: {Key}", key);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            _keys.TryGetValue(key, out var registration);
            _cache.Remove(key);
            if (registration is not null)
            {
                RemoveRegistration(registration);
            }

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
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            // Convert Redis pattern to regex (simple implementation)
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            var regex = new System.Text.RegularExpressions.Regex(regexPattern);

            var matchingKeys = _keys
                .Where(pair => regex.IsMatch(pair.Key))
                .ToList();

            foreach (var (key, registration) in matchingKeys)
            {
                _cache.Remove(key);
                RemoveRegistration(registration);
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
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

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
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<long>(cancellationToken);
        }

        CacheKeyRegistration? registration = null;
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
                }

                registration = new CacheKeyRegistration(this, key);
                _keys[key] = registration;

                // Set the new value with TTL
                _cache.Set(key, newValue, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    Size = 1 // One explicit entry unit for the bounded local cache.
                }.RegisterPostEvictionCallback(static (_, _, _, state) =>
                {
                    if (state is CacheKeyRegistration evicted
                        && evicted.Owner.TryGetTarget(out var owner))
                    {
                        owner.RemoveRegistration(evicted);
                    }
                }, registration));

                return Task.FromResult(newValue);
            }
        }
        catch (Exception ex)
        {
            if (registration is not null)
            {
                RemoveRegistration(registration);
            }

            _logger.LogWarning(ex, "Failed to increment in-memory cache key: {Key}", key);
            return Task.FromResult(0L);
        }
    }

    private void RemoveRegistration(CacheKeyRegistration registration)
    {
        ((ICollection<KeyValuePair<string, CacheKeyRegistration>>)_keys)
            .Remove(new KeyValuePair<string, CacheKeyRegistration>(registration.Key, registration));
    }

    private sealed class CacheKeyRegistration(InMemoryCacheService owner, string key)
    {
        public WeakReference<InMemoryCacheService> Owner { get; } = new(owner);

        public string Key { get; } = key;
    }
}
