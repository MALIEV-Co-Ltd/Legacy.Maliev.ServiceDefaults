namespace Maliev.Aspire.ServiceDefaults.Caching;

/// <summary>
/// Interface for a standardized caching service in the Maliev ecosystem.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a value from the cache.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cached value, or null if not found.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sets a value in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="ttl">The time-to-live for the cached value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all keys matching a specific pattern.
    /// </summary>
    /// <param name="pattern">The key pattern (e.g., "users:*").</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>True if the key exists, otherwise false.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments a value in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ttl">The time-to-live for the cached value (only applied if the key is new).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The new value after incrementing.</returns>
    Task<long> IncrementAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
}
