using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maliev.Aspire.ServiceDefaults.LegacyAuth;

/// <summary>Runtime-only credentials used with the legacy AuthService service-login contract.</summary>
public sealed class LegacyServiceAuthenticationOptions
{
    /// <summary>Configuration section containing the legacy workload identity.</summary>
    public const string SectionName = "ServiceAuthentication";

    /// <summary>Registered legacy service client identifier.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Secret projected only at runtime from the consolidated legacy secret.</summary>
    [Required, StringLength(4096, MinimumLength = 1)]
    public string ClientSecret { get; set; } = string.Empty;
}

/// <summary>Provides cached, short-lived access tokens issued by the legacy AuthService.</summary>
public interface ILegacyServiceAccessTokenProvider
{
    /// <summary>Gets a usable token, or null when service authentication is unavailable.</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Invalidates a cached token rejected by a downstream service.</summary>
    void Invalidate(string token);
}

/// <summary>Exchanges a runtime-only client credential for a bounded, cached legacy service token.</summary>
public sealed class LegacyServiceAccessTokenProvider : ILegacyServiceAccessTokenProvider
{
    /// <summary>Named client reserved for the unauthenticated legacy service-login exchange.</summary>
    public const string HttpClientName = "LegacyAuthServiceTokenExchange";

    private const int MaximumResponseBytes = 32 * 1024;
    private const int MaximumTokenLength = 16 * 1024;
    private const int MaximumLifetimeSeconds = 3600;
    private readonly IHttpClientFactory clientFactory;
    private readonly LegacyServiceAuthenticationOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LegacyServiceAccessTokenProvider> logger;
    private readonly object stateLock = new();
    private CacheEntry? cachedToken;
    private Task<CacheEntry?>? activeRefresh;

    /// <summary>Initializes the legacy service token provider.</summary>
    public LegacyServiceAccessTokenProvider(
        IHttpClientFactory clientFactory,
        IOptions<LegacyServiceAuthenticationOptions> options,
        TimeProvider timeProvider,
        ILogger<LegacyServiceAccessTokenProvider> logger)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<CacheEntry?> refresh;
        lock (stateLock)
        {
            if (IsUsable(cachedToken)) return cachedToken!.Token;
            if (activeRefresh is null)
            {
                var createdRefresh = ExchangeAsync();
                activeRefresh = createdRefresh;
                _ = createdRefresh.ContinueWith(
                    static (completed, state) => ((LegacyServiceAccessTokenProvider)state!).CompleteRefresh(completed),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                refresh = createdRefresh;
            }
            else
            {
                refresh = activeRefresh;
            }
        }

        var result = await refresh.WaitAsync(cancellationToken);
        return result?.Token;
    }

    /// <inheritdoc />
    public void Invalidate(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (stateLock)
        {
            if (cachedToken is not null && string.Equals(cachedToken.Token, token, StringComparison.Ordinal))
                cachedToken = null;
        }
    }

    private bool IsUsable(CacheEntry? entry) => entry is not null && timeProvider.GetUtcNow() < entry.RefreshAtUtc;

    private void CompleteRefresh(Task<CacheEntry?> completed)
    {
        lock (stateLock)
        {
            if (completed.Status == TaskStatus.RanToCompletion && completed.Result is not null)
                cachedToken = completed.Result;
            else if (completed.IsFaulted)
                _ = completed.Exception;
            if (ReferenceEquals(activeRefresh, completed)) activeRefresh = null;
        }
    }

    private async Task<CacheEntry?> ExchangeAsync()
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            options.ClientId.Length > 128 ||
            !string.Equals(options.ClientId, options.ClientId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.ClientSecret) ||
            options.ClientSecret.Length > 4096)
        {
            logger.LogWarning("Legacy service authentication is not configured with a valid runtime identity.");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/service/login")
            {
                Content = JsonContent.Create(new ServiceLoginRequest(options.ClientId, options.ClientSecret))
            };
            using var response = await clientFactory.CreateClient(HttpClientName).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Legacy AuthService rejected a service identity with status {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                logger.LogWarning("Legacy AuthService returned an oversized service-login response.");
                return null;
            }

            var content = await ReadBoundedContentAsync(response.Content);
            if (content is null)
            {
                logger.LogWarning("Legacy AuthService returned an oversized service-login response.");
                return null;
            }

            var login = JsonSerializer.Deserialize<ServiceLoginResponse>(content);
            if (login is null ||
                string.IsNullOrWhiteSpace(login.AccessToken) ||
                login.AccessToken.Length > MaximumTokenLength ||
                login.ExpiresIn is < 60 or > MaximumLifetimeSeconds)
            {
                logger.LogWarning("Legacy AuthService returned an invalid service-login response.");
                return null;
            }

            var now = timeProvider.GetUtcNow();
            var lifetime = TimeSpan.FromSeconds(login.ExpiresIn);
            var refreshSkew = TimeSpan.FromSeconds(Math.Min(120, Math.Max(1, login.ExpiresIn / 5)));
            return new CacheEntry(login.AccessToken, now + lifetime - refreshSkew);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException)
        {
            logger.LogWarning("Legacy AuthService was unavailable or returned a malformed service-login response.");
            return null;
        }
    }

    private static async Task<byte[]?> ReadBoundedContentAsync(HttpContent content)
    {
        await using var stream = await content.ReadAsStreamAsync(CancellationToken.None);
        using var buffer = new MemoryStream(MaximumResponseBytes);
        var block = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(block, CancellationToken.None);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes) return null;
            await buffer.WriteAsync(block.AsMemory(0, read), CancellationToken.None);
        }
    }

    private sealed record CacheEntry(string Token, DateTimeOffset RefreshAtUtc);
    private sealed record ServiceLoginRequest(
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("clientSecret")] string ClientSecret);
    private sealed record ServiceLoginResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
}
