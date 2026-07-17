using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Default implementation of IAM service client for permission checking and resolution.
/// Supports both global and resource-scoped permission checks with automatic failover.
/// Results are cached briefly per (principal, permission, resource) tuple — without it
/// every HTTP request re-checks identical permissions and floods the IAM service with
/// hundreds of duplicate check-permission calls per second.
/// </summary>
public partial class IamServiceClient : IIamServiceClient
{
    private const string AspireTestAdminPrincipalId = "00000000-0000-0000-0000-000000000002";
    private const string LiveCheckCredentialConfigurationKey = "IAM:LivePermissionChecks:Credential";
    private const string LiveCheckCredentialHeaderName = "X-Maliev-IAM-Live-Check-Key";

    // Allowed results are stable enough to reuse for a minute; denials expire fast so
    // newly granted permissions propagate within seconds.
    private static readonly TimeSpan AllowedCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeniedCacheTtl = TimeSpan.FromSeconds(5);
    private const int CacheCleanupThreshold = 2048;
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    // Static: the client is registered scoped, so an instance cache would reset on
    // every request. Keys embed the principal, so sharing across scopes is safe.
    private static readonly ConcurrentDictionary<string, CachedPermissionResult> _permissionCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inFlightPermissionChecks = new();
    private static int _missingLiveCheckCredentialLogged;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IamServiceClient> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a client without live-check credentials for compatibility with existing callers.
    /// Standard checks remain available; live checks fail closed.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named clients.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="environment">The current host environment.</param>
    public IamServiceClient(
        IHttpClientFactory httpClientFactory,
        ILogger<IamServiceClient> logger,
        IHostEnvironment environment)
        : this(httpClientFactory, logger, environment, EmptyConfiguration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IamServiceClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named clients.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="environment">The current host environment.</param>
    /// <param name="configuration">The application configuration.</param>
    public IamServiceClient(
        IHttpClientFactory httpClientFactory,
        ILogger<IamServiceClient> logger,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _environment = environment;
        _configuration = configuration;
    }

    private HttpClient GetHttpClient() => _httpClientFactory.CreateClient("IAMService");

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetUserPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (IsAspireTestAdmin(userId))
        {
            return ["*"];
        }

        try
        {
            var response = await GetHttpClient().PostAsJsonAsync(
                "/iam/v1/auth/resolve-permissions",
                new { PrincipalId = userId },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.FailedToFetchPermissions(_logger, userId, response.StatusCode);
                return Enumerable.Empty<string>();
            }

            var result = await response.Content.ReadFromJsonAsync<PermissionResolutionResponse>(cancellationToken: cancellationToken);
            return result?.Permissions ?? Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            Log.ErrorFetchingPermissions(_logger, userId, ex);
            return Enumerable.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckPermissionAsync(
        string principalId,
        string permissionId,
        string? resourcePath = null,
        CancellationToken cancellationToken = default)
    {
        if (IsAspireTestAdmin(principalId))
        {
            return true;
        }

        var cacheKey = $"{principalId}|{permissionId}|{resourcePath ?? "global"}";
        if (_permissionCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Allowed;
        }

        var lazyCheck = new Lazy<Task<bool>>(
            () => FetchAndCachePermissionAsync(
                principalId,
                permissionId,
                resourcePath,
                cacheKey,
                bypassCache: false,
                liveCheckCredential: null,
                CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var activeCheck = _inFlightPermissionChecks.GetOrAdd(cacheKey, lazyCheck);

        try
        {
            // Callers can cancel their wait without canceling the shared upstream fetch.
            return await activeCheck.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (activeCheck.Value.IsCompleted)
            {
                _inFlightPermissionChecks.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(cacheKey, activeCheck));
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> CheckPermissionLiveAsync(
        string principalId,
        string permissionId,
        string? resourcePath = null,
        CancellationToken cancellationToken = default)
    {
        if (IsAspireTestAdmin(principalId))
        {
            return Task.FromResult(true);
        }

        var liveCheckCredential = _configuration[LiveCheckCredentialConfigurationKey];
        if (string.IsNullOrWhiteSpace(liveCheckCredential))
        {
            if (Interlocked.Exchange(ref _missingLiveCheckCredentialLogged, 1) == 0)
            {
                Log.MissingLiveCheckCredential(_logger);
            }

            return Task.FromResult(false);
        }

        var cacheKey = $"{principalId}|{permissionId}|{resourcePath ?? "global"}";
        return FetchAndCachePermissionAsync(
            principalId,
            permissionId,
            resourcePath,
            cacheKey,
            bypassCache: true,
            liveCheckCredential,
            cancellationToken);
    }

    private async Task<bool> FetchAndCachePermissionAsync(
        string principalId,
        string permissionId,
        string? resourcePath,
        string cacheKey,
        bool bypassCache,
        string? liveCheckCredential,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new CheckPermissionRequest(principalId, permissionId, resourcePath, bypassCache);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/iam/v1/auth/check-permission")
            {
                Content = JsonContent.Create(request)
            };
            if (!string.IsNullOrWhiteSpace(liveCheckCredential))
            {
                requestMessage.Headers.Add(LiveCheckCredentialHeaderName, liveCheckCredential);
            }

            using var response = await GetHttpClient().SendAsync(requestMessage, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.FailedToCheckPermission(_logger, principalId, permissionId, resourcePath ?? "global", response.StatusCode);
                // Transport/availability failures are NOT cached — the next call retries.
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<CheckPermissionResponse>(cancellationToken: cancellationToken);
            var allowed = result?.Allowed ?? false;
            CachePermissionResult(cacheKey, allowed);
            return allowed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ErrorCheckingPermission(_logger, principalId, permissionId, resourcePath ?? "global", ex);
            return false;
        }
    }

    private void CachePermissionResult(string cacheKey, bool allowed)
    {
        if (_permissionCache.Count >= CacheCleanupThreshold)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _permissionCache)
            {
                if (entry.Value.ExpiresAtUtc <= now)
                {
                    _permissionCache.TryRemove(entry.Key, out _);
                }
            }
        }

        var ttl = allowed ? AllowedCacheTtl : DeniedCacheTtl;
        _permissionCache[cacheKey] = new CachedPermissionResult(allowed, DateTime.UtcNow.Add(ttl));
    }

    private readonly record struct CachedPermissionResult(bool Allowed, DateTime ExpiresAtUtc);

    /// <inheritdoc />
    public async Task<Dictionary<string, bool>> CheckPermissionsAsync(
        string principalId,
        IEnumerable<PermissionCheckRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>();

        try
        {
            var requestList = requests.ToList();

            // Performance optimization: Parallelize permission checks using Task.WhenAll
            // Each permission check is an independent HTTP call that can run concurrently
            var tasks = requestList.Select(async req =>
            {
                var allowed = await CheckPermissionAsync(
                    principalId,
                    req.PermissionId,
                    req.ResourcePath,
                    cancellationToken);

                return new { req.PermissionId, Allowed = allowed };
            }).ToList();

            var results = await Task.WhenAll(tasks);

            foreach (var r in results)
            {
                result[r.PermissionId] = r.Allowed;
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.ErrorBulkCheckingPermissions(_logger, principalId, requests.Count(), ex);

            // Return false for all permissions on error
            foreach (var req in requests)
            {
                result[req.PermissionId] = false;
            }

            return result;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetAuthorizedResourcesAsync(
        string principalId,
        string permissionId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        if (IsAspireTestAdmin(principalId))
        {
            return ["*"];
        }

        try
        {
            var response = await GetHttpClient().GetAsync(
                $"/iam/v1/auth/authorized-resources?principalId={principalId}&permissionId={permissionId}&resourceType={resourceType}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch authorized resources for principal {PrincipalId}. Status: {StatusCode}", principalId, response.StatusCode);
                return Enumerable.Empty<string>();
            }

            var result = await response.Content.ReadFromJsonAsync<AuthorizedResourcesResponse>(cancellationToken: cancellationToken);
            return result?.ResourceIds ?? Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching authorized resources for principal {PrincipalId}", principalId);
            return Enumerable.Empty<string>();
        }
    }

    // DTOs for IAM API communication
    private sealed record AuthorizedResourcesResponse(
        string PrincipalId,
        string PermissionId,
        string ResourceType,
        List<string> ResourceIds);

    private sealed record PermissionResolutionResponse(
        string PrincipalId,
        List<string> Permissions,
        List<string> Roles,
        DateTime CacheUntil,
        bool FromCache);

    private sealed record CheckPermissionRequest(
        string PrincipalId,
        string PermissionId,
        string? ResourcePath,
        bool BypassCache);

    private sealed record CheckPermissionResponse(
        string PrincipalId,
        string PermissionId,
        bool Allowed,
        string? ResourcePath,
        bool FromCache,
        long LatencyMs);

    private bool IsAspireTestAdmin(string principalId)
    {
        return _environment.IsEnvironment("Testing") &&
            string.Equals(principalId, AspireTestAdminPrincipalId, StringComparison.OrdinalIgnoreCase);
    }

    // Logging with source generation
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch permissions for user {UserId}. Status: {StatusCode}")]
        public static partial void FailedToFetchPermissions(ILogger logger, string userId, System.Net.HttpStatusCode statusCode);

        [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while fetching permissions for user {UserId}")]
        public static partial void ErrorFetchingPermissions(ILogger logger, string userId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to check permission {Permission} for principal {PrincipalId} on resource {ResourcePath}. Status: {StatusCode}")]
        public static partial void FailedToCheckPermission(ILogger logger, string principalId, string permission, string resourcePath, System.Net.HttpStatusCode statusCode);

        [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while checking permission {Permission} for principal {PrincipalId} on resource {ResourcePath}")]
        public static partial void ErrorCheckingPermission(ILogger logger, string principalId, string permission, string resourcePath, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while bulk checking {Count} permissions for principal {PrincipalId}")]
        public static partial void ErrorBulkCheckingPermissions(ILogger logger, string principalId, int count, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "IAM live permission check denied because its dedicated credential is not configured")]
        public static partial void MissingLiveCheckCredential(ILogger logger);
    }
}
