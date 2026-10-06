using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

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

    // Scoped clients share a bucket only through their host's singleton HTTP factory.
    // Equal principals or IAM URLs do not establish trust between independent hosts.
    private static readonly ConditionalWeakTable<IHttpClientFactory, PermissionCacheState> PermissionCaches = new();
    private readonly ConcurrentDictionary<string, CachedPermissionResult> _permissionCache;
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inFlightPermissionChecks;
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
        var cache = PermissionCaches.GetValue(httpClientFactory, static _ => new PermissionCacheState());
        _permissionCache = cache.Results;
        _inFlightPermissionChecks = cache.InFlight;
    }

    private HttpClient GetHttpClient() => _httpClientFactory.CreateClient("IAMService");

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetUserPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (IsAspireTestAdmin(userId))
        {
            return ["*"];
        }

        var observation = new PrivateDependencyFailureObservation();
        try
        {
            var client = GetHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/iam/v1/auth/resolve-permissions")
            {
                Content = JsonContent.Create(new { PrincipalId = userId }),
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            using var response = await client.SendWithPrivateFailureObservationAsync(request, cancellationToken, observation, HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(observation, "ResolvePermissions", "HttpStatus", response.StatusCode);
                return Enumerable.Empty<string>();
            }

            var result = await response.Content.ReadFromJsonAsync<PermissionResolutionResponse>(cancellationToken: cancellationToken);
            return result?.Permissions ?? Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            RecordFailure(observation, "ResolvePermissions", FailureKind(ex));
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
        var observation = new PrivateDependencyFailureObservation();
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

            using var response = await GetHttpClient().SendWithPrivateFailureObservationAsync(requestMessage, cancellationToken, observation, HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(observation, "CheckPermission", "HttpStatus", response.StatusCode);
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
            RecordFailure(observation, "CheckPermission", FailureKind(ex));
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

    private sealed class PermissionCacheState
    {
        internal ConcurrentDictionary<string, CachedPermissionResult> Results { get; } = new();
        internal ConcurrentDictionary<string, Lazy<Task<bool>>> InFlight { get; } = new();
    }

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
            RecordFailure(null, "BulkCheckPermissions", FailureKind(ex));

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

        var observation = new PrivateDependencyFailureObservation();
        try
        {
            var client = GetHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/iam/v1/auth/authorized-resources?principalId={principalId}&permissionId={permissionId}&resourceType={resourceType}")
            {
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            using var response = await client.SendWithPrivateFailureObservationAsync(request, cancellationToken, observation, HttpCompletionOption.ResponseContentRead);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(observation, "AuthorizedResources", "HttpStatus", response.StatusCode);
                return Enumerable.Empty<string>();
            }

            var result = await response.Content.ReadFromJsonAsync<AuthorizedResourcesResponse>(cancellationToken: cancellationToken);
            return result?.ResourceIds ?? Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            RecordFailure(observation, "AuthorizedResources", FailureKind(ex));
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

    private static string FailureKind(Exception exception) => exception switch
    {
        System.Text.Json.JsonException => "InvalidJson",
        HttpRequestException => "Transport",
        OperationCanceledException => "Cancelled",
        TimeoutException => "Timeout",
        _ => "Unexpected",
    };

    private void RecordFailure(PrivateDependencyFailureObservation? observation, string operation, string kind,
        System.Net.HttpStatusCode? status = null)
    {
        // Selected transport observation owns its event even if its provider threw.
        // Body parsing and unselected clients still receive one safe local event.
        if (observation?.WasObserved == true) return;
        try
        {
            if (status is { } code)
                _logger.LogWarning(new EventId(5102, "IamOperationFailure"),
                    "IAM operation failed Operation={Operation} FailureKind={FailureKind} StatusCode={StatusCode}",
                    operation, kind, (int)code);
            else
                _logger.LogError(new EventId(5102, "IamOperationFailure"),
                    "IAM operation failed Operation={Operation} FailureKind={FailureKind}", operation, kind);
        }
        catch (Exception)
        {
            // Logging providers must not replace the existing fail-closed result or cancellation contract.
        }
    }

    // Missing credentials are a code-owned configuration signal without identity details.
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "IAM live permission check denied because its dedicated credential is not configured")]
        public static partial void MissingLiveCheckCredential(ILogger logger);
    }
}
