using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.Middleware;

/// <summary>
/// Middleware that assigns a correlation ID to each HTTP request for distributed tracing.
/// The correlation ID is read from the X-Correlation-ID header if present, or generated as a new GUID.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeaderName = "X-Correlation-ID";
    internal static readonly object ValidatedCorrelationIdKey = new();
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the CorrelationIdMiddleware.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger for correlation ID operations.</param>
    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request and assigns or propagates a correlation ID.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);

        // Add to response headers
        context.Response.Headers.TryAdd(CorrelationIdHeaderName, correlationId);

        // Add to HttpContext.Items for access in controllers/services
        context.Items["CorrelationId"] = correlationId;
        context.Items[ValidatedCorrelationIdKey] = correlationId;

        // Create logging scope
        var scopeProperties = new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["RouteTemplate"] = RouteLogPath.FromContext(context),
            ["RequestMethod"] = context.Request.Method
        };

        using (_logger.BeginScope(scopeProperties))
        {
            await _next(context);
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out var supplied) &&
            supplied.Count == 1 && supplied[0] is { Length: > 0 and <= 64 } candidate &&
            candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return candidate;
        }

        return Guid.NewGuid().ToString();
    }
}
