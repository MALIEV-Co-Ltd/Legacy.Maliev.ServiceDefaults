using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Localization.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class LocalizationRouteCompatibilityTests
{
    [Theory]
    [InlineData("th-TH", "th-TH")]
    [InlineData("TH-th", "th-TH")]
    [InlineData("en-US", "en-US")]
    public async Task Matching_route_culture_preserves_case_insensitive_pass_through(
        string requestedCulture, string actualCulture)
    {
        await AssertPassThrough(requestedCulture, actualCulture, hasRouter: true);
    }

    [Theory]
    [InlineData(null, "th-TH")]
    [InlineData("unsupported", "th-TH")]
    [InlineData("th-TH", null)]
    public async Task Missing_legacy_router_preserves_pass_through_even_when_culture_differs(
        string? requestedCulture, string? actualCulture)
    {
        // This specifically characterizes the legacy-router fallback, not modern endpoint redirects.
        await AssertPassThrough(requestedCulture, actualCulture);
    }

    [Theory]
    [InlineData("{language}/quotation", 302, "/th-TH/quotation")]
    [InlineData("{language:regex(^en-US$)}/quotation", 404, null)]
    public async Task Unsupported_route_uses_actual_culture_or_returns_not_found_when_generation_fails(
        string routeTemplate, int expectedStatus, string? expectedLocation)
    {
        var type = typeof(PrivateStartupBoundary).Assembly.GetType(
            "Maliev.Middleware.Localization.RedirectUnsupportedCulturesMiddleware");
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        using var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
        var options = new RequestLocalizationOptions();
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new RouteDataRequestCultureProvider { RouteDataStringKey = "language" });
        var context = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Values["language"] = "unsupported";
        routeData.Routers.Add(new Route(new RouteHandler(_ => Task.CompletedTask), routeTemplate,
            services.GetRequiredService<IInlineConstraintResolver>()));
        context.Features.Set<IRoutingFeature>(new RoutingFeature { RouteData = routeData });
        context.Request.RouteValues = routeData.Values;
        context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture("th-TH"), null));
        var calls = 0;
        RequestDelegate next = _ => { calls++; return Task.CompletedTask; };
        var middleware = Activator.CreateInstance(type, next, options);
        Assert.NotNull(middleware);
        var invoke = type.GetMethod("Invoke", [typeof(HttpContext)]);
        Assert.NotNull(invoke);

        await Assert.IsAssignableFrom<Task>(invoke.Invoke(middleware, [context]));

        Assert.Equal(0, calls);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        if (expectedLocation is null)
            Assert.False(context.Response.Headers.ContainsKey("Location"));
        else
            Assert.Equal(expectedLocation, context.Response.Headers.Location.ToString());
    }

    private static async Task AssertPassThrough(string? requestedCulture, string? actualCulture, bool hasRouter = false)
    {
        var type = typeof(PrivateStartupBoundary).Assembly.GetType(
            "Maliev.Middleware.Localization.RedirectUnsupportedCulturesMiddleware");
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        using var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
        var options = new RequestLocalizationOptions();
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new RouteDataRequestCultureProvider { RouteDataStringKey = "language" });
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        var routeData = new RouteData();
        if (hasRouter)
            routeData.Routers.Add(new Route(new RouteHandler(_ => Task.CompletedTask),
                "{language}/quotation", services.GetRequiredService<IInlineConstraintResolver>()));
        routeData.Values["language"] = requestedCulture;
        context.Features.Set<IRoutingFeature>(new RoutingFeature { RouteData = routeData });
        context.Request.RouteValues = routeData.Values;
        if (actualCulture is not null)
            context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(
                new RequestCulture(actualCulture), null));
        var calls = 0;
        RequestDelegate next = actual =>
        {
            Assert.Same(context, actual);
            calls++;
            actual.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        };
        var middleware = Activator.CreateInstance(type, next, options);
        Assert.NotNull(middleware);
        var invoke = type.GetMethod("Invoke", [typeof(HttpContext)]);
        Assert.NotNull(invoke);

        await Assert.IsAssignableFrom<Task>(invoke.Invoke(middleware, [context]));

        Assert.Equal(1, calls);
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }
}
