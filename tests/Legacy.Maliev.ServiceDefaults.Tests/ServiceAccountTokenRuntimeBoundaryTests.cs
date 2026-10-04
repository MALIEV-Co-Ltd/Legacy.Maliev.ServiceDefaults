using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Existing local-signing behavior characterization, not central IAM authorization proof.
public sealed class ServiceAccountTokenRuntimeBoundaryTests
{
    [Theory]
    [InlineData("pem")]
    [InlineData("encoded-pem")]
    [InlineData("pkcs8")]
    public void RsaKeyEncodings_ProduceVerifiableIdentityWithConfiguredBoundedLifetime(string encoding)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var configured = encoding switch
        {
            "pem" => pem,
            "encoded-pem" => Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)),
            "pkcs8" => Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
            _ => throw new ArgumentException("Unknown fixture encoding.", nameof(encoding))
        };
        var values = Configuration("Production");
        values["Jwt:PrivateKey"] = configured;
        values["IAM:TokenExpirationMinutes"] = "7";
        var before = DateTime.UtcNow;
        var token = new ServiceAccountTokenProvider(Build(values), "UploadService", "fixture-registration-owner").GetToken();
        var after = DateTime.UtcNow;

        var jwt = Validate(token, new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
        Assert.Equal("fixture-registration-owner", Assert.Single(jwt.Claims, claim => claim.Type == "sub").Value);
        Assert.Equal("UploadService", Assert.Single(jwt.Claims, claim => claim.Type == "service_name").Value);
        Assert.Equal("service", Assert.Single(jwt.Claims, claim => claim.Type == "user_type").Value);
        Assert.Equal("iam-registration", Assert.Single(jwt.Claims, claim => claim.Type == "purpose").Value);
        Assert.Equal("service-account", Assert.Single(jwt.Claims, claim => claim.Type == "role").Value);
        // Characterizes the legacy signing contract only; no permission resolver is bypassed.
        Assert.Equal("*", Assert.Single(jwt.Claims, claim => claim.Type == "permissions").Value);
        Assert.InRange(jwt.ValidFrom, before.AddSeconds(-1), after);
        Assert.InRange(jwt.ValidTo, before.AddMinutes(7).AddSeconds(-1), after.AddMinutes(7));
        Assert.InRange((jwt.ValidTo - jwt.ValidFrom).TotalSeconds, 419d, 421d);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void ExplicitNonProductionFallback_SignsWithGeneratedHmacKeyAndDefaultLifetime(string environment)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var signingText = Convert.ToBase64String(bytes);
        var values = Configuration(environment);
        values["Jwt:SecurityKey"] = signingText;
        var before = DateTime.UtcNow;
        var token = new ServiceAccountTokenProvider(Build(values), "CustomerService").GetToken();
        var after = DateTime.UtcNow;

        var jwt = Validate(token, new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingText)), SecurityAlgorithms.HmacSha256);
        Assert.Equal("system:service:customer", Assert.Single(jwt.Claims, claim => claim.Type == "sub").Value);
        Assert.InRange(jwt.ValidTo, before.AddHours(1).AddSeconds(-1), after.AddHours(1));
        Assert.InRange((jwt.ValidTo - jwt.ValidFrom).TotalSeconds, 3599d, 3601d);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestingFallback_MissingOrUndersizedSigningMaterialCannotIssueToken(bool shortKey)
    {
        var values = Configuration("Testing");
        if (shortKey) values["Jwt:SecurityKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(4));
        var provider = new ServiceAccountTokenProvider(Build(values), "CustomerService");
        Assert.Throws<InvalidOperationException>(() => provider.GetToken());
    }

    [Fact]
    public void ConfiguredMalformedRsaKey_DoesNotSilentlyDowngradeToAllowedHmacFallback()
    {
        var values = Configuration("Testing");
        values["Jwt:PrivateKey"] = "invalid-fixture-private-key-encoding";
        values["Jwt:SecurityKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ServiceAccountTokenProvider(Build(values), "CustomerService");
        Assert.Throws<FormatException>(() => provider.GetToken());
    }

    private static Dictionary<string, string?> Configuration(string environment) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = environment,
        ["Jwt:Issuer"] = "https://issuer.example.test",
        ["Jwt:Audience"] = "fixture-registration"
    };

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static JwtSecurityToken Validate(string token, SecurityKey key, string algorithm)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://issuer.example.test",
            ValidateAudience = true,
            ValidAudience = "fixture-registration",
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidAlgorithms = [algorithm]
        }, out var validated);
        return Assert.IsType<JwtSecurityToken>(validated);
    }
}
