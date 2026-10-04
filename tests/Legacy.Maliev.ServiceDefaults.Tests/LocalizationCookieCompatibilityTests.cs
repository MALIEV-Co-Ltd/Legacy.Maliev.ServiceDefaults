using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Xml.Linq;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class LocalizationCookieCompatibilityTests
{
    [Fact]
    public void Packaged_localization_documentation_retains_public_type_and_member_descriptions()
    {
        var xmlPath = Path.ChangeExtension(typeof(PrivateStartupBoundary).Assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), "Localization XML documentation must accompany the shared assembly.");
        var members = XDocument.Load(xmlPath).Descendants("member")
            .ToDictionary(member => Assert.IsType<string>(member.Attribute("name")?.Value), StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "T:Maliev.Middleware.Localization.RequestLocalizationCookiesMiddleware",
            "T:Maliev.Middleware.Localization.RequestLocalizationCookiesMiddlewareExtensions",
            "M:Maliev.Middleware.Localization.RequestLocalizationCookiesMiddleware.InvokeAsync(Microsoft.AspNetCore.Http.HttpContext,Microsoft.AspNetCore.Http.RequestDelegate)",
            "M:Maliev.Middleware.Localization.RequestLocalizationCookiesMiddlewareExtensions.UseRequestLocalizationCookies(Microsoft.AspNetCore.Builder.IApplicationBuilder)",
            "T:Maliev.Middleware.Localization.RedirectUnsupportedCulturesMiddleware",
            "M:Maliev.Middleware.Localization.RedirectUnsupportedCulturesMiddleware.Invoke(Microsoft.AspNetCore.Http.HttpContext)",
        })
        {
            Assert.True(members.TryGetValue(name, out var member), $"Missing localization XML member {name}");
            Assert.NotNull(member);
            Assert.False(string.IsNullOrWhiteSpace(member.Element("summary")?.Value));
        }
    }

    [Fact]
    public void Public_cookie_middleware_remains_an_IMiddleware_with_the_original_extension_surface()
    {
        var middleware = RequireCookieMiddleware();
        Assert.True(typeof(IMiddleware).IsAssignableFrom(middleware));
        Assert.Equal(typeof(CookieRequestCultureProvider), middleware.GetProperty("Provider")?.PropertyType);
        var extensions = typeof(PrivateStartupBoundary).Assembly.GetType(
            "Maliev.Middleware.Localization.RequestLocalizationCookiesMiddlewareExtensions");
        Assert.NotNull(extensions);
        Assert.True(extensions.IsPublic);
        var method = extensions.GetMethod("UseRequestLocalizationCookies", [typeof(IApplicationBuilder)]);
        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.Equal(typeof(IApplicationBuilder), method.ReturnType);
    }

    [Fact]
    public void Provider_selects_the_first_configured_cookie_provider_not_a_new_default()
    {
        var options = new RequestLocalizationOptions();
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
        var first = new CookieRequestCultureProvider { CookieName = "first-culture" };
        options.RequestCultureProviders.Add(first);
        options.RequestCultureProviders.Add(new CookieRequestCultureProvider { CookieName = "second-culture" });

        var middleware = CreateMiddleware(options);

        Assert.Same(first, middleware.GetType().GetProperty("Provider")!.GetValue(middleware));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_provider_or_culture_feature_invokes_next_without_creating_a_cookie(
        bool hasProvider, bool hasFeature)
    {
        var options = new RequestLocalizationOptions();
        options.RequestCultureProviders.Clear();
        if (hasProvider)
            options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
        var context = new DefaultHttpContext();
        if (hasFeature)
            context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(
                new RequestCulture("th-TH", "en-US"), null));
        var calls = 0;

        await CreateMiddleware(options).InvokeAsync(context, actual =>
        {
            Assert.Same(context, actual);
            calls++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Culture_and_UI_culture_are_written_to_the_configured_cookie_before_next()
    {
        var options = new RequestLocalizationOptions();
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new CookieRequestCultureProvider { CookieName = "legacy-culture" });
        var context = new DefaultHttpContext();
        context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(
            new RequestCulture("th-TH", "en-US"), null));
        var calls = 0;

        await CreateMiddleware(options).InvokeAsync(context, actual =>
        {
            Assert.Same(context, actual);
            AssertCultureCookie(actual);
            calls++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        AssertCultureCookie(context);
    }

    [Theory]
    [InlineData("th-TH", "en-US", "th-TH|en-US")]
    [InlineData("en-US", "th-TH", "en-US|th-TH")]
    public async Task Public_extension_writes_selected_cultures_through_the_real_DI_and_HTTP_pipeline(
        string culture, string uiCulture, string expectedBody)
    {
        var type = RequireCookieMiddleware();
        var extensions = typeof(PrivateStartupBoundary).Assembly.GetType(
            "Maliev.Middleware.Localization.RequestLocalizationCookiesMiddlewareExtensions");
        Assert.NotNull(extensions);
        Assert.True(extensions.IsPublic);
        var method = extensions.GetMethod("UseRequestLocalizationCookies", [typeof(IApplicationBuilder)]);
        Assert.NotNull(method);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLocalization();
        builder.Services.AddTransient(type);
        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture("en-US").AddSupportedCultures("en-US", "th-TH")
                .AddSupportedUICultures("en-US", "th-TH");
            options.RequestCultureProviders.Clear();
            options.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
            options.RequestCultureProviders.Add(new CookieRequestCultureProvider { CookieName = "legacy-culture" });
        });
        await using var app = builder.Build();
        app.UseRequestLocalization();
        Assert.Same(app, method.Invoke(null, [app]));
        app.Run(context => context.Response.WriteAsync(
            $"{System.Globalization.CultureInfo.CurrentCulture.Name}|{System.Globalization.CultureInfo.CurrentUICulture.Name}"));
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/?culture={culture}&ui-culture={uiCulture}");

        response.EnsureSuccessStatusCode();
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        Assert.Equal($"legacy-culture=c={culture}|uic={uiCulture}; path=/",
            Uri.UnescapeDataString(Assert.Single(response.Headers.GetValues("Set-Cookie"))));
    }

    private static void AssertCultureCookie(HttpContext context) => Assert.Equal(
        "legacy-culture=c=th-TH|uic=en-US; path=/",
        Uri.UnescapeDataString(Assert.Single(context.Response.Headers.SetCookie)!));

    private static IMiddleware CreateMiddleware(RequestLocalizationOptions options) =>
        Assert.IsAssignableFrom<IMiddleware>(Activator.CreateInstance(
            RequireCookieMiddleware(), Options.Create(options)));

    private static Type RequireCookieMiddleware()
    {
        var type = typeof(PrivateStartupBoundary).Assembly.GetType(
            "Maliev.Middleware.Localization.RequestLocalizationCookiesMiddleware");
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        return type;
    }
}
