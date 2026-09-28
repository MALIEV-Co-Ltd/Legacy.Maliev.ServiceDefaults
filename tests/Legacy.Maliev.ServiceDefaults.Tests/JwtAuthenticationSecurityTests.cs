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

    [Fact]
    public void AddJwtAuthentication_TestingRegistersLocalValidatorAndCallerOverrides()
    {
        var builder = CreateBuilder("Testing", new Dictionary<string, string?>
        {
            ["Jwt:SecurityKey"] = SecurityKey,
        });
        builder.AddJwtAuthentication(options => options.SaveToken = true);

        using var serviceProvider = builder.Services.BuildServiceProvider();
        JwtBearerOptions options = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.True(options.SaveToken);
        Assert.False(options.MapInboundClaims);
        Assert.False(options.TokenValidationParameters.ValidateIssuer);
        Assert.False(options.TokenValidationParameters.ValidateAudience);
        Assert.False(options.TokenValidationParameters.ValidateLifetime);
        Assert.False(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.NotNull(options.TokenValidationParameters.SignatureValidator);
        Assert.Contains(options.TokenValidationParameters.IssuerSigningKeys, key => key.KeyId == "test-symmetric-key");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AddJwtAuthentication_DevelopmentSymmetricFallbackRequiresExplicitAllowance(bool allowed, bool succeeds)
    {
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:AllowSymmetricValidation"] = allowed.ToString(),
        });

        if (!succeeds)
        {
            Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthentication());
            return;
        }

        builder.AddJwtAuthentication();
        using var provider = builder.Services.BuildServiceProvider();
        JwtBearerOptions options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.IsType<SymmetricSecurityKey>(options.TokenValidationParameters.IssuerSigningKey);
        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
    }

    [Fact]
    public void AddJwtAuthenticationSymmetric_ProductionAlwaysRejects()
    {
        var builder = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });
        Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthenticationSymmetric());
    }

    [Fact]
    public void AddJwtAuthenticationSymmetric_MissingKeyAndTrustBoundaryFailClosed()
    {
        var missingKey = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });
        Assert.Throws<InvalidOperationException>(() => missingKey.AddJwtAuthenticationSymmetric());

        var missingAudience = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
        });
        Assert.Throws<InvalidOperationException>(() => missingAudience.AddJwtAuthenticationSymmetric());
    }

    [Fact]
    public void AddJwtAuthentication_InvalidEncodedPublicKeyFailsBeforeRegistration()
    {
        var invalidBase64 = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = "not-base64",
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });
        Assert.Throws<FormatException>(() => invalidBase64.AddJwtAuthentication());
    }

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

        var handler = new JwtSecurityTokenHandler();
        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecurityKey));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, symmetricKey, SecurityAlgorithms.HmacSha256, Issuer, Audience),
            options.TokenValidationParameters, out _));
    }

    [Fact]
    public void AddJwtAuthentication_ProductionRejectsRsaAlgorithmsOtherThanRs256()
    {
        using var rsa = RSA.Create(2048);
        var builder = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = ExportPublicKey(rsa),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        builder.AddJwtAuthentication();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var parameters = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler();
        var signingKey = new RsaSecurityKey(rsa);
        var rs256 = CreateToken(handler, signingKey, SecurityAlgorithms.RsaSha256, Issuer, Audience);
        var rs384 = CreateToken(handler, signingKey, SecurityAlgorithms.RsaSha384, Issuer, Audience);

        handler.ValidateToken(rs256, parameters, out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(rs384, parameters, out _));
    }

    [Fact]
    public void AddJwtAuthentication_DevelopmentDualKeyAcceptsOnlyRs256AndHs256()
    {
        using var rsa = RSA.Create(2048);
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = ExportPublicKey(rsa),
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:AllowSymmetricValidation"] = "true"
        });
        builder.AddJwtAuthentication();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var parameters = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler();
        var rsaKey = new RsaSecurityKey(rsa);
        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecurityKey));

        handler.ValidateToken(CreateToken(handler, rsaKey, SecurityAlgorithms.RsaSha256, Issuer, Audience), parameters, out _);
        handler.ValidateToken(CreateToken(handler, symmetricKey, SecurityAlgorithms.HmacSha256, Issuer, Audience), parameters, out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, rsaKey, SecurityAlgorithms.RsaSha384, Issuer, Audience), parameters, out _));
    }

    [Fact]
    public void AddJwtAuthentication_DevelopmentSymmetricOptOutRejectsHs256()
    {
        using var rsa = RSA.Create(2048);
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = ExportPublicKey(rsa),
            ["Jwt:SecurityKey"] = SecurityKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:AllowSymmetricValidation"] = "false"
        });
        builder.AddJwtAuthentication();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var parameters = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler();
        var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecurityKey));

        handler.ValidateToken(CreateToken(handler, new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256, Issuer, Audience), parameters, out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, symmetricKey, SecurityAlgorithms.HmacSha256, Issuer, Audience), parameters, out _));
    }

    [Fact]
    public void AddJwtAuthentication_ProductionRejectsWrongIssuerAudienceAndSignature()
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var builder = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = ExportPublicKey(rsa),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        builder.AddJwtAuthentication();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var parameters = serviceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler();
        var signingKey = new RsaSecurityKey(rsa);

        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, signingKey, SecurityAlgorithms.RsaSha256, "https://other.example", Audience),
            parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, signingKey, SecurityAlgorithms.RsaSha256, Issuer, "other-audience"),
            parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            CreateToken(handler, new RsaSecurityKey(otherRsa), SecurityAlgorithms.RsaSha256, Issuer, Audience),
            parameters, out _));
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
    /// Production must fail closed when a public signing key is present but the
    /// token trust boundary is incomplete.
    /// </summary>
    [Theory]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void AddJwtAuthentication_ProductionMissingTrustBoundary_Throws(string missingKey)
    {
        using var rsa = RSA.Create(2048);
        var values = new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = ExportPublicKey(rsa),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        };
        values[missingKey] = null;

        var builder = CreateBuilder(Environments.Production, values);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthentication());

        Assert.Contains("Jwt:Issuer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jwt:Audience", exception.Message, StringComparison.Ordinal);
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

    private static string CreateToken(
        JwtSecurityTokenHandler handler,
        SecurityKey key,
        string algorithm,
        string issuer,
        string audience)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, algorithm));
        return handler.WriteToken(token);
    }
}
