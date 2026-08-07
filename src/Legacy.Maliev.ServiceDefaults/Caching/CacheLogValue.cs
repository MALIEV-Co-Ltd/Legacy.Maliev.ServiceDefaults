using System.Security.Cryptography;
using System.Text;

namespace Maliev.Aspire.ServiceDefaults.Caching;

/// <summary>
/// Produces non-reversible diagnostic identifiers for caller-supplied cache keys.
/// Cache keys can contain emails, session identifiers, or other sensitive values,
/// so they must never be copied into application logs verbatim.
/// </summary>
internal static class CacheLogValue
{
    public static string Hash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "empty";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{value.Length}:{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }
}
