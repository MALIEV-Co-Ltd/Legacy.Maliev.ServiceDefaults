using Maliev.Aspire.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring JWT authentication in the application.
/// </summary>
public static class AuthenticationExtensions
{
    private const string TestSymmetricSecurityKey = "test-key-at-least-32-characters-long-for-integration-tests"; // gitleaks:allow

    /// <summary>
    /// Adds JWT Bearer authentication with RSA public key validation.
    /// Reads configuration from:
    /// - Jwt:PublicKey (Base64-encoded RSA public key in PEM format)
    /// - Jwt:Issuer
    /// - Jwt:Audience
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configureOptions">Optional action to configure JWT bearer options.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddJwtAuthentication(
        this IHostApplicationBuilder builder,
        Action<JwtBearerOptions>? configureOptions = null)
    {
        var publicKeyBase64 = builder.Configuration["Jwt:PublicKey"];
        var securityKey = builder.Configuration["Jwt:SecurityKey"];
        var issuer = builder.Configuration["Jwt:Issuer"];
        var audience = builder.Configuration["Jwt:Audience"];

        if (builder.Environment.IsEnvironment("Testing"))
        {
            var testingSigningKeys = CreateTestingSigningKeys(publicKeyBase64, securityKey);

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.MapInboundClaims = false;
                    // Testing uses JwtSecurityTokenHandler's SignatureValidator hook for local tokens.
#pragma warning disable CS0618
                    options.UseSecurityTokenValidators = true;
#pragma warning restore CS0618

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = false,
                        ValidateIssuerSigningKey = false,
                        IssuerSigningKeys = testingSigningKeys,
                        NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub,
                        RoleClaimType = "role",
                        SignatureValidator = delegate (string token, TokenValidationParameters parameters)
                        {
                            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                            return handler.ReadJwtToken(token);
                        }
                    };

                    configureOptions?.Invoke(options);
                });

            builder.Services.AddPermissionAuthorization();
            return builder;
        }

        if (string.IsNullOrEmpty(publicKeyBase64))
        {
            if (!string.IsNullOrEmpty(securityKey) && IsSymmetricValidationAllowed(builder))
            {
                return builder.AddJwtAuthenticationSymmetric(configureOptions);
            }

            throw new InvalidOperationException(
                "JWT PublicKey not configured. Set Jwt:PublicKey in configuration. " +
                "Symmetric Jwt:SecurityKey validation is allowed only in Development or Testing.");
        }

        // Decode Base64 PEM public key
        var publicKeyPem = Encoding.UTF8.GetString(Convert.FromBase64String(publicKeyBase64));
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);

        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrEmpty(issuer),
            ValidIssuer = issuer,
            ValidateAudience = !string.IsNullOrEmpty(audience),
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(5), // Allow 5 minutes clock skew
            NameClaimType = "sub",
            RoleClaimType = "role"
        };

        if (!string.IsNullOrEmpty(securityKey) && IsSymmetricValidationAllowed(builder))
        {
            var symmetricKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey));
            tokenValidationParameters.IssuerSigningKeys = new SecurityKey[]
            {
                new RsaSecurityKey(rsa),
                symmetricKey
            };
        }
        else
        {
            tokenValidationParameters.IssuerSigningKey = new RsaSecurityKey(rsa);
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Disable claim type mapping to keep original claim names like "sub" instead of URIs
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokenValidationParameters;

                // Development convenience: Allow HTTP metadata if not in Production
                if (!builder.Environment.IsProduction())
                {
                    options.RequireHttpsMetadata = false;
                }

                // Apply custom configuration if provided
                configureOptions?.Invoke(options);
            });


        builder.Services.AddPermissionAuthorization();

        return builder;
    }

    /// <summary>
    /// Adds JWT Bearer authentication with symmetric key (HMAC-SHA256) validation.
    /// Reads configuration from:
    /// - Jwt:SecurityKey (secret key)
    /// - Jwt:Issuer
    /// - Jwt:Audience
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configureOptions">Optional action to configure JWT bearer options.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddJwtAuthenticationSymmetric(
        this IHostApplicationBuilder builder,
        Action<JwtBearerOptions>? configureOptions = null)
    {
        var securityKey = builder.Configuration["Jwt:SecurityKey"];
        var issuer = builder.Configuration["Jwt:Issuer"];
        var audience = builder.Configuration["Jwt:Audience"];

        if (!IsSymmetricValidationAllowed(builder))
        {
            throw new InvalidOperationException(
                "Symmetric JWT validation is allowed only in Development or Testing. " +
                "Configure Jwt:PublicKey and use AddJwtAuthentication() for shared service authentication.");
        }

        if (string.IsNullOrEmpty(securityKey))
        {
            throw new InvalidOperationException("JWT SecurityKey not configured. Set Jwt:SecurityKey in configuration.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Disable claim type mapping to keep original claim names like "sub" instead of URIs
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = !string.IsNullOrEmpty(issuer),
                    ValidIssuer = issuer,
                    ValidateAudience = !string.IsNullOrEmpty(audience),
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ClockSkew = TimeSpan.FromMinutes(5),
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };

                // Apply custom configuration if provided
                configureOptions?.Invoke(options);
            });

        builder.Services.AddPermissionAuthorization();

        return builder;
    }

    private static bool IsSymmetricValidationAllowed(IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsEnvironment("Testing"))
        {
            return true;
        }

        if (!builder.Environment.IsDevelopment())
        {
            return false;
        }

        return builder.Configuration.GetValue("Jwt:AllowSymmetricValidation", true);
    }

    private static IReadOnlyCollection<SecurityKey> CreateTestingSigningKeys(
        string? publicKeyBase64,
        string? securityKey)
    {
        var keys = new List<SecurityKey>
        {
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSymmetricSecurityKey))
            {
                KeyId = "test-symmetric-key"
            }
        };

        if (!string.IsNullOrWhiteSpace(securityKey))
        {
            keys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey)));
        }

        if (!string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            var publicKeyPem = Encoding.UTF8.GetString(Convert.FromBase64String(publicKeyBase64));
            var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            keys.Add(new RsaSecurityKey(rsa));
        }

        return keys;
    }
}
