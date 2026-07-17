namespace Maliev.Aspire.ServiceDefaults;

/// <summary>
/// Standard rate limiting policy names used across the Maliev platform.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// Standard authenticated business operations.
    /// Default: 100 req/min
    /// </summary>
    public const string Api = "api";

    /// <summary>
    /// Unauthenticated or public endpoints.
    /// Default: 50 req/min
    /// </summary>
    public const string Public = "public";

    /// <summary>
    /// Administrative, management, or bulk data update tools.
    /// Default: 25 req/min
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Resource-intensive bulk operations (imports, reports).
    /// Default: 10 req/min
    /// </summary>
    public const string Batch = "batch";

    /// <summary>
    /// Sensitive identity operations (token exchange, login).
    /// Default: 5 req/min
    /// </summary>
    public const string Auth = "auth";

    /// <summary>
    /// Explicitly for high-volume, lightweight read operations.
    /// Default: 200 req/min
    /// </summary>
    public const string Read = "read";

    /// <summary>
    /// Explicitly for sensitive or expensive data mutations.
    /// Default: 50 req/min
    /// </summary>
    public const string Write = "write";
}
