using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace Maliev.Middleware.Localization;

/// <summary>Registers the compatibility culture-cookie middleware.</summary>
public static class RequestLocalizationCookiesMiddlewareExtensions
{
    /// <summary>Remembers the request's selected culture in its configured cookie.</summary>
    /// <param name="app">The application pipeline.</param>
    /// <returns>The same application builder.</returns>
    public static IApplicationBuilder UseRequestLocalizationCookies(this IApplicationBuilder app)
    {
        app.UseMiddleware<RequestLocalizationCookiesMiddleware>();
        return app;
    }
}

/// <summary>Preserves culture and UI culture using the first configured cookie provider.</summary>
public class RequestLocalizationCookiesMiddleware : IMiddleware
{
    /// <summary>Gets the first configured cookie provider, if one exists.</summary>
    public CookieRequestCultureProvider? Provider { get; }

    /// <summary>Initializes the middleware with the request-localization configuration.</summary>
    /// <param name="requestLocalizationOptions">The configured localization options.</param>
    public RequestLocalizationCookiesMiddleware(IOptions<RequestLocalizationOptions> requestLocalizationOptions)
    {
        Provider = requestLocalizationOptions.Value.RequestCultureProviders
            .OfType<CookieRequestCultureProvider>().FirstOrDefault();
    }

    /// <summary>Writes the selected culture cookie before invoking the next middleware.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="next">The next middleware.</param>
    /// <returns>The asynchronous pipeline operation.</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (Provider is not null && context.Features.Get<IRequestCultureFeature>() is { } feature)
            context.Response.Cookies.Append(Provider.CookieName,
                CookieRequestCultureProvider.MakeCookieValue(feature.RequestCulture));

        await next(context);
    }
}
