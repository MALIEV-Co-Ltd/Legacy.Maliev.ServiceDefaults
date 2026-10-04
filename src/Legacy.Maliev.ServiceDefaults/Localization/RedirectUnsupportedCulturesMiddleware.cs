using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Localization.Routing;
using Microsoft.AspNetCore.Routing;

namespace Maliev.Middleware.Localization;

/// <summary>Preserves the legacy route-culture redirect and missing-router fallback.</summary>
public class RedirectUnsupportedCulturesMiddleware
{
    private readonly RequestDelegate next;
    private readonly string routeDataStringKey;

    /// <summary>Initializes the middleware with the configured route-culture provider.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="options">The request-localization configuration.</param>
    public RedirectUnsupportedCulturesMiddleware(RequestDelegate next, RequestLocalizationOptions options)
    {
        this.next = next;
        var provider = options.RequestCultureProviders.OfType<RouteDataRequestCultureProvider>().FirstOrDefault();
        routeDataStringKey = provider!.RouteDataStringKey;
    }

    /// <summary>Redirects unsupported route cultures when a legacy router can generate a path.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The asynchronous pipeline operation.</returns>
    public async Task Invoke(HttpContext context)
    {
        var requestedCulture = context.GetRouteValue(routeDataStringKey)?.ToString();
        var actualCulture = context.Features.Get<IRequestCultureFeature>()?.RequestCulture.Culture.Name;
        if (string.IsNullOrEmpty(requestedCulture) ||
            !string.Equals(requestedCulture, actualCulture, StringComparison.OrdinalIgnoreCase))
        {
            var routeData = context.GetRouteData();
            if (routeData is null || routeData.Routers.Count == 0)
            {
                await next(context);
                return;
            }

            var newCulturedPath = GetNewPath(context, actualCulture);
            if (string.IsNullOrEmpty(newCulturedPath))
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            else
                context.Response.Redirect(newCulturedPath);
            return;
        }

        await next(context);
    }

    private string GetNewPath(HttpContext context, string? newCulture)
    {
        var routeData = context.GetRouteData();
        var router = routeData.Routers[0];
        var pathContext = new VirtualPathContext(context, routeData.Values,
            new RouteValueDictionary { { routeDataStringKey, newCulture } });
        if (router.GetVirtualPath(pathContext) is null)
            return string.Empty;
        return router.GetVirtualPath(pathContext)!.VirtualPath;
    }
}
