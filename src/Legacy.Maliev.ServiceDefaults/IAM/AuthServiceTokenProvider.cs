using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Supplies centrally issued service access tokens from AuthService.
/// </summary>
public interface IAuthServiceTokenProvider
{
    /// <summary>
    /// Gets a validated service access token.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for a shared refresh.</param>
    /// <returns>A validated RS256 bearer token.</returns>
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for the opt-in AuthService service-token exchange.
/// </summary>
public sealed class AuthServiceTokenExchangeOptions
{
    /// <summary>
    /// Configuration section containing service authentication credentials.
    /// </summary>
    public const string SectionName = "ServiceAuthentication";

    /// <summary>
    /// Gets or sets the AuthService client identifier.
    /// </summary>
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AuthService client secret.
    /// </summary>
    [Required]
    [StringLength(512, MinimumLength = 16)]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of seconds removed from the effective token expiry before refresh.
    /// </summary>
    [Range(5, 300)]
    public int RefreshSafetyMarginSeconds { get; set; } = 60;
}

/// <summary>
/// Binds the centrally issued token to the service identity supplied by application code.
/// </summary>
/// <param name="ServiceName">The canonical process service name expected in the token.</param>
public sealed record ServiceProcessIdentity(string ServiceName);

/// <summary>
/// Raised when AuthService cannot provide a trustworthy service access token.
/// </summary>
public sealed class ServiceTokenExchangeException : Exception
{
    /// <summary>
    /// Initializes a safe service-token exchange failure.
    /// </summary>
    /// <param name="message">A non-sensitive diagnostic message.</param>
    public ServiceTokenExchangeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a safe service-token exchange failure with an internal cause.
    /// </summary>
    /// <param name="message">A non-sensitive diagnostic message.</param>
    /// <param name="innerException">The internal failure cause.</param>
    public ServiceTokenExchangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exchanges client credentials for a short-lived AuthService token and validates the complete trust response.
/// </summary>
public sealed class AuthServiceTokenProvider : IAuthServiceTokenProvider, IDisposable
{
    /// <summary>
    /// Named client used exclusively for the unauthenticated client-credential exchange.
    /// </summary>
    public const string HttpClientName = "AuthServiceTokenExchange";

    private const int MaximumServiceTokenLifetimeSeconds = 3600;
    private const int ExpiryConsistencyToleranceSeconds = 5;
    private static readonly string[] AuthorityClaimTypes = ["permissions", "permission", "roles", "role"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthServiceTokenExchangeOptions _options;
    private readonly ServiceProcessIdentity _processIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthServiceTokenProvider> _logger;
    private readonly RSA _signingKey;
    private readonly TokenValidationParameters _validationParameters;
    private readonly object _stateLock = new();
    private CacheEntry? _cachedToken;
    private Task<CacheEntry>? _activeRefresh;

    /// <summary>
    /// Initializes the AuthService-backed token provider.
    /// </summary>
    public AuthServiceTokenProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<AuthServiceTokenExchangeOptions> options,
        ServiceProcessIdentity processIdentity,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<AuthServiceTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _processIdentity = processIdentity ?? throw new ArgumentNullException(nameof(processIdentity));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        (_signingKey, _validationParameters) = CreateValidationState(
            configuration ?? throw new ArgumentNullException(nameof(configuration)));
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<CacheEntry> refreshTask;
        lock (_stateLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cachedToken is not null && now < _cachedToken.RefreshAtUtc)
            {
                return _cachedToken.Token;
            }

            if (_activeRefresh is { IsFaulted: true } failedRefresh)
            {
                _ = failedRefresh.Exception;
                _activeRefresh = null;
            }

            if (_activeRefresh is null)
            {
                var newRefresh = ExchangeAndValidateAsync();
                _activeRefresh = newRefresh;
                _ = newRefresh.ContinueWith(
                    static (completedRefresh, state) =>
                        ((AuthServiceTokenProvider)state!).ObserveAndClearFailedRefresh(completedRefresh),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                refreshTask = newRefresh;
            }
            else
            {
                refreshTask = _activeRefresh;
            }
        }

        try
        {
            var refreshed = await refreshTask.WaitAsync(cancellationToken);
            lock (_stateLock)
            {
                _cachedToken = refreshed;
            }

            return refreshed.Token;
        }
        finally
        {
            if (refreshTask.IsCompleted)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeRefresh, refreshTask))
                    {
                        _activeRefresh = null;
                    }
                }
            }
        }
    }

    private void ObserveAndClearFailedRefresh(Task<CacheEntry> completedRefresh)
    {
        _ = completedRefresh.Exception;
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeRefresh, completedRefresh))
            {
                _activeRefresh = null;
            }
        }
    }

    private async Task<CacheEntry> ExchangeAndValidateAsync()
    {
        try
        {
            var exchangeStartedAt = _timeProvider.GetUtcNow();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/service/login")
            {
                Content = JsonContent.Create(new ServiceLoginRequest(_options.ClientId, _options.ClientSecret))
            };
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AuthService token exchange failed for process {ServiceName} with status {StatusCode}",
                    _processIdentity.ServiceName,
                    (int)response.StatusCode);
                throw new ServiceTokenExchangeException(
                    $"AuthService token exchange failed with status {(int)response.StatusCode}.");
            }

            var exchange = await response.Content.ReadFromJsonAsync<ServiceLoginResponse>(cancellationToken: CancellationToken.None);
            if (exchange is null)
            {
                throw new ServiceTokenExchangeException("AuthService returned an empty token response.");
            }

            return ValidateResponse(exchange, exchangeStartedAt);
        }
        catch (ServiceTokenExchangeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException)
        {
            throw new ServiceTokenExchangeException("AuthService token exchange was unavailable or malformed.");
        }
    }

    private CacheEntry ValidateResponse(ServiceLoginResponse response, DateTimeOffset exchangeStartedAt)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken) ||
            !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            response.ExpiresIn is < 60 or > MaximumServiceTokenLifetimeSeconds ||
            response.User is null)
        {
            throw new ServiceTokenExchangeException("AuthService returned an invalid token response.");
        }

        ClaimsPrincipal principal;
        JwtSecurityToken jwt;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            principal = handler.ValidateToken(response.AccessToken, _validationParameters, out var validatedToken);
            jwt = validatedToken as JwtSecurityToken
                ?? throw new SecurityTokenException("Validated token was not a JWT.");
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            throw new ServiceTokenExchangeException("AuthService returned an invalid service token.");
        }

        if (!string.Equals(jwt.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal) ||
            !HasSingleClaim(principal, "client_id", _options.ClientId) ||
            !HasSingleClaim(principal, "service_name", _processIdentity.ServiceName) ||
            !HasSingleClaim(principal, "user_type", "service") ||
            AuthorityClaimTypes.Any(type => principal.FindAll(type).Any(claim => claim.Value == "*")))
        {
            throw new ServiceTokenExchangeException("AuthService returned an inconsistent service identity or authority.");
        }

        var subjects = principal.FindAll(JwtRegisteredClaimNames.Sub).Select(claim => claim.Value).ToArray();
        var subject = subjects.Length == 1 ? subjects[0] : null;
        var issuedAtValue = principal.FindAll(JwtRegisteredClaimNames.Iat).Select(claim => claim.Value).ToArray();
        if (subject is null ||
            !Guid.TryParseExact(subject, "D", out var subjectId) ||
            !string.Equals(subject, subjectId.ToString("D"), StringComparison.Ordinal) ||
            issuedAtValue.Length != 1 ||
            !long.TryParse(issuedAtValue[0], out var issuedAtSeconds) ||
            DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds) > _timeProvider.GetUtcNow() ||
            !string.Equals(response.User.UserId, _options.ClientId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(response.User.PrincipalId, "D", out var responsePrincipalId) ||
            !string.Equals(response.User.PrincipalId, responsePrincipalId.ToString("D"), StringComparison.Ordinal) ||
            responsePrincipalId != subjectId ||
            !string.Equals(response.User.UserType, "service", StringComparison.Ordinal) ||
            !string.Equals(response.User.Name, _processIdentity.ServiceName, StringComparison.Ordinal))
        {
            throw new ServiceTokenExchangeException("AuthService returned inconsistent service response metadata.");
        }

        var now = _timeProvider.GetUtcNow();
        var jwtExpiresAt = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        var responseExpiresAt = exchangeStartedAt.AddSeconds(response.ExpiresIn);
        var expiryTolerance = TimeSpan.FromSeconds(ExpiryConsistencyToleranceSeconds);
        var earliestConsistentExpiry = responseExpiresAt - expiryTolerance;
        var latestConsistentExpiry = now.AddSeconds(response.ExpiresIn) + expiryTolerance;
        if (jwtExpiresAt <= now ||
            jwtExpiresAt < earliestConsistentExpiry ||
            jwtExpiresAt > latestConsistentExpiry)
        {
            throw new ServiceTokenExchangeException("AuthService returned inconsistent service token expiry.");
        }

        var effectiveExpiry = jwtExpiresAt <= responseExpiresAt ? jwtExpiresAt : responseExpiresAt;
        var effectiveLifetimeSeconds = Math.Max(1, (int)(effectiveExpiry - now).TotalSeconds);
        var safetyMarginSeconds = Math.Min(
            _options.RefreshSafetyMarginSeconds,
            Math.Max(1, effectiveLifetimeSeconds / 5));
        var refreshAt = effectiveExpiry.AddSeconds(-safetyMarginSeconds);
        if (refreshAt <= now)
        {
            throw new ServiceTokenExchangeException("AuthService returned a service token with insufficient usable lifetime.");
        }

        return new CacheEntry(response.AccessToken, refreshAt);
    }

    private static bool HasSingleClaim(ClaimsPrincipal principal, string claimType, string expectedValue)
    {
        var values = principal.FindAll(claimType).Select(claim => claim.Value).ToArray();
        return values.Length == 1 && string.Equals(values[0], expectedValue, StringComparison.Ordinal);
    }

    internal static bool HasValidTrustConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!TryGetHttpsTrustIdentifier(configuration["Jwt:Issuer"], out _) ||
            !TryGetHttpsTrustIdentifier(configuration["Jwt:Audience"], out _))
        {
            return false;
        }

        try
        {
            using var rsa = LoadRsaSubjectPublicKey(configuration["Jwt:PublicKey"]);
            return rsa.KeySize >= 2048;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _signingKey.Dispose();

    private (RSA SigningKey, TokenValidationParameters ValidationParameters) CreateValidationState(
        IConfiguration configuration)
    {
        if (!TryGetHttpsTrustIdentifier(configuration["Jwt:Issuer"], out var issuer) ||
            !TryGetHttpsTrustIdentifier(configuration["Jwt:Audience"], out var audience))
        {
            throw new InvalidOperationException(
                "Jwt:Issuer and Jwt:Audience must be canonical absolute HTTPS identifiers.");
        }

        var rsa = LoadRsaSubjectPublicKey(configuration["Jwt:PublicKey"]);
        try
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    return notBefore.HasValue &&
                        notBefore.Value <= now &&
                        expires.HasValue &&
                        expires.Value > now;
                },
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = "roles"
            };

            return (rsa, validationParameters);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static RSA LoadRsaSubjectPublicKey(string? configuredPublicKey)
    {
        if (string.IsNullOrWhiteSpace(configuredPublicKey))
        {
            throw new InvalidOperationException("Jwt:PublicKey is required.");
        }

        try
        {
            var publicKeyPem = configuredPublicKey.Contains("BEGIN", StringComparison.Ordinal)
                ? configuredPublicKey
                : Encoding.UTF8.GetString(Convert.FromBase64String(configuredPublicKey));
            var pem = publicKeyPem.AsSpan();
            if (!PemEncoding.TryFind(pem, out var fields) ||
                !pem[fields.Label].SequenceEqual("PUBLIC KEY"))
            {
                throw new InvalidOperationException("Jwt:PublicKey must contain an RSA subject public key.");
            }

            var prefixEnd = fields.Location.Start.GetOffset(pem.Length);
            var suffixStart = fields.Location.End.GetOffset(pem.Length);
            if (!pem[..prefixEnd].Trim().IsEmpty || !pem[suffixStart..].Trim().IsEmpty)
            {
                throw new InvalidOperationException("Jwt:PublicKey must contain one subject public key.");
            }

            var subjectPublicKeyInfo = Convert.FromBase64String(pem[fields.Base64Data].ToString());
            try
            {
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
                    if (bytesRead != subjectPublicKeyInfo.Length || rsa.KeySize < 2048)
                    {
                        throw new InvalidOperationException("Jwt:PublicKey must be an RSA key of at least 2048 bits.");
                    }

                    return rsa;
                }
                catch
                {
                    rsa.Dispose();
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("Jwt:PublicKey is not a valid RSA subject public key.", exception);
        }
    }

    private static bool TryGetHttpsTrustIdentifier(string? configuredValue, out string value)
    {
        value = configuredValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var canonicalValue = uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped).TrimEnd('/');
        return string.Equals(value, canonicalValue, StringComparison.Ordinal);
    }

    private sealed record CacheEntry(string Token, DateTimeOffset RefreshAtUtc);

    private sealed record ServiceLoginRequest(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret")] string ClientSecret);

    private sealed record ServiceLoginResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user")] ServiceIdentityResponse? User);

    private sealed record ServiceIdentityResponse(
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("principal_id")] string? PrincipalId,
        [property: JsonPropertyName("user_type")] string? UserType,
        [property: JsonPropertyName("name")] string? Name);
}
