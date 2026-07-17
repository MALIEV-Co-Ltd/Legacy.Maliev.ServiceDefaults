using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Maliev.Aspire.ServiceDefaults.Testing;

/// <summary>
/// Provides helper methods for generating test JWT tokens and configuring test authentication in integration tests.
/// </summary>
public static class IAMTestHelpers
{
    private static RSA? _testRsa;

    /// <summary>
    /// Sets the RSA key used for signing test tokens.
    /// If not set, symmetric signing will be used.
    /// </summary>
    public static void SetTestRSA(RSA rsa) => _testRsa = rsa;

    /// <summary>
    /// Creates a test JWT with specified permissions
    /// </summary>
    public static string CreateTestJWT(string principalId, string issuer = "test", string audience = "test", params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, principalId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("client_id", "test-client")
        };

        foreach (var permission in permissions)
        {
            claims.Add(new Claim("permissions", permission));
        }

        SigningCredentials creds;
        if (_testRsa != null)
        {
            var key = new RsaSecurityKey(_testRsa) { KeyId = "test-rsa-key" };
            creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        }
        else
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-key-at-least-32-characters-long-for-integration-tests")) { KeyId = "test-symmetric-key" }; // gitleaks:allow
            creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Adds authentication with test JWT to HttpClient
    /// </summary>
    public static HttpClient WithTestAuth(this HttpClient client, string userId = "test-user", string issuer = "test", string audience = "test", params string[] permissions)
    {
        var token = CreateTestJWT(userId, issuer, audience, permissions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
