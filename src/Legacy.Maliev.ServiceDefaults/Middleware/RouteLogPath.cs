using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Maliev.Aspire.ServiceDefaults.Middleware;

internal static class RouteLogPath
{
    internal static string FromContext(HttpContext context)
    {
        // Literal path segments can carry tokens or customer data. Route patterns
        // are application-defined; unknown routes receive a fixed fallback.
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return string.IsNullOrEmpty(pattern) ? "/" : pattern;
    }
}
