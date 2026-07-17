using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>
/// Security regression tests for shared JWT authentication defaults.
/// </summary>
public class JwtAuthenticationSecurityTests
{
    private const string Issuer = "https://api.maliev.com";
    private const string Audience = "https://api.maliev.com";
    private const string SecurityKey = "test-key-at-least-32-characters-long"; // gitleaks:allow

    /// <summary>
    /// Production RSA validation must not also trust the shared HMAC key.
    /// </summary>
    [Fact]
    public void AddJwtAuthentication_ProductionWithPublicAndSecurityKey_UsesOnlyRsaSigningKey()
    {
        using var rsa = RSA.Create(2048);
        var builder = CreateBuilder(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Jwt:PublicKey"] = ExportPublicKey(rsa),
                ["Jwt:SecurityKey"] = SecurityKey,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience
            });

        builder.AddJwtAuthentication();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.IsType<RsaSecurityKey>(options.TokenValidationParameters.IssuerSigningKey);
        Assert.Null(options.TokenValidationParameters.IssuerSigningKeys);
    }

    /// <summary>
    /// Production must fail closed when only the legacy HMAC key is configured.
    /// </summary>
    [Fact]
    public void AddJwtAuthentication_ProductionWithOnlySecurityKey_Throws()
    {
        var builder = CreateBuilder(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["Jwt:SecurityKey"] = SecurityKey,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience
            });

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthentication());

        Assert.Contains("Jwt:PublicKey", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Service-account tokens should prefer RS256 whenever the RSA private key is configured.
    /// </summary>
    [Fact]
    public void ServiceAccountTokenProvider_WithPrivateKey_EmitsRs256Token()
    {
        using var rsa = RSA.Create(2048);
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["Jwt:PrivateKey"] = ExportPrivateKey(rsa),
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        var provider = new ServiceAccountTokenProvider(configuration, "UploadService");

        var token = provider.GetToken();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);

        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        }, out _);
    }

    /// <summary>
    /// Production service-account signing must not silently fall back to the shared HMAC key.
    /// </summary>
    [Fact]
    public void ServiceAccountTokenProvider_ProductionWithoutPrivateKey_Throws()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Production,
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        var provider = new ServiceAccountTokenProvider(configuration, "UploadService");

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetToken());

        Assert.Contains("Jwt:PrivateKey", exception.Message, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder CreateBuilder(
        string environmentName,
        Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName
        });

        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static string ExportPrivateKey(RSA rsa)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
    }

    private static string ExportPublicKey(RSA rsa)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()));
    }
}
