using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Provides service account JWT tokens for IAM registration.
/// </summary>
public interface IServiceAccountTokenProvider
{
    /// <summary>
    /// Gets the service account token for IAM registration.
    /// Auto-generates tokens on each call with short expiration for security.
    /// </summary>
    string GetToken();
}

/// <summary>
/// Auto-generates service account JWT tokens for IAM registration.
/// Tokens are generated on-demand with configurable expiration.
/// </summary>
public class ServiceAccountTokenProvider : IServiceAccountTokenProvider
{
    private readonly IConfiguration _configuration;
    private readonly string _serviceName;
    private readonly string? _subOverride;

    /// <param name="configuration">App configuration.</param>
    /// <param name="serviceName">Service name used to build the sub claim.</param>
    /// <param name="subOverride">Optional explicit sub claim value (e.g. "system"). When set, overrides the computed system:service:{name} value.</param>
    public ServiceAccountTokenProvider(IConfiguration configuration, string serviceName, string? subOverride = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _subOverride = subOverride;
    }

    /// <summary>
    /// Generates a new service account token on each call.
    /// Token is valid for 1 hour by default.
    /// </summary>
    public string GetToken()
    {
        var privateKey = _configuration["Jwt:PrivateKey"];
        var securityKey = _configuration["Jwt:SecurityKey"];

        // Standard issuer/audience for all Maliev services
        var issuer = _configuration["Jwt:Issuer"] ?? "https://api.maliev.com";
        var audience = _configuration["Jwt:Audience"] ?? "https://api.maliev.com";

        // Token expiration (default: 1 hour, configurable)
        var expirationMinutes = _configuration.GetValue<int?>("IAM:TokenExpirationMinutes") ?? 60;

        return GenerateToken(
            serviceName: _serviceName,
            privateKey: privateKey,
            securityKey: securityKey,
            issuer: issuer,
            audience: audience,
            expirationMinutes: expirationMinutes,
            symmetricFallbackAllowed: IsSymmetricSigningAllowed(),
            subOverride: _subOverride
        );
    }

    private static string GenerateToken(
        string serviceName,
        string? privateKey,
        string? securityKey,
        string issuer,
        string audience,
        int expirationMinutes,
        bool symmetricFallbackAllowed,
        string? subOverride = null)
    {
        var serviceNameLower = serviceName.ToLowerInvariant().Replace("service", "");
        var sub = subOverride ?? $"system:service:{serviceNameLower}";

        var claims = new[]
        {
            new Claim("sub", sub),
            new Claim("service_name", serviceName),
            new Claim("user_type", "service"),
            new Claim("role", "service-account"),
            new Claim("purpose", "iam-registration"),
            new Claim("iss", issuer),
            new Claim("aud", audience),
            new Claim("permissions", "*") // Full access for service accounts in platform
        };

        var credentials = CreateSigningCredentials(privateKey, securityKey, symmetricFallbackAllowed);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private bool IsSymmetricSigningAllowed()
    {
        var environmentName = _configuration["ASPNETCORE_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
    }

    private static SigningCredentials CreateSigningCredentials(
        string? privateKey,
        string? securityKey,
        bool symmetricFallbackAllowed)
    {
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            return new SigningCredentials(CreateRsaSecurityKey(privateKey), SecurityAlgorithms.RsaSha256);
        }

        if (!symmetricFallbackAllowed)
        {
            throw new InvalidOperationException(
                "Jwt:PrivateKey not configured. Service account tokens require RS256 signing outside Development or Testing.");
        }

        if (string.IsNullOrWhiteSpace(securityKey))
        {
            throw new InvalidOperationException(
                "Jwt:SecurityKey not configured. Required for development/test service account token fallback.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey));

        if (key.KeySize < 256)
        {
            throw new InvalidOperationException("Jwt:SecurityKey must be at least 256 bits (32 characters) for HMAC-SHA256.");
        }

        return new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    private static RsaSecurityKey CreateRsaSecurityKey(string privateKey)
    {
        var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(DecodePem(privateKey));
        }
        catch (ArgumentException)
        {
            var keyBytes = Convert.FromBase64String(privateKey);
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }

        return new RsaSecurityKey(rsa);
    }

    private static string DecodePem(string configuredKey)
    {
        if (configuredKey.Contains("BEGIN", StringComparison.Ordinal))
        {
            return configuredKey;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(configuredKey));
        }
        catch (FormatException)
        {
            return configuredKey;
        }
    }
}

/// <summary>
/// Utility for generating service account tokens (for development/testing).
/// </summary>
public static class ServiceAccountTokenGenerator
{
    /// <summary>
    /// Generates a service account JWT token for IAM registration.
    /// </summary>
    /// <param name="serviceName">Name of the service (e.g., "ChatbotService")</param>
    /// <param name="securityKey">JWT signing key</param>
    /// <param name="issuer">Token issuer</param>
    /// <param name="audience">Token audience</param>
    /// <param name="expirationYears">Token expiration in years (default: 10)</param>
    /// <returns>JWT token string</returns>
    public static string GenerateToken(
        string serviceName,
        string securityKey,
        string issuer,
        string audience,
        int expirationYears = 10)
    {
        var claims = new[]
        {
            new Claim("sub", $"system:service:{serviceName.ToLowerInvariant()}"),
            new Claim("service_name", serviceName),
            new Claim("user_type", "service"),
            new Claim("role", "service-account"),
            new Claim("purpose", "iam-registration"),
            new Claim("permissions", "*")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddYears(expirationYears),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
