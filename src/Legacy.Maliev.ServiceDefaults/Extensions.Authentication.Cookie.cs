using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers the shared Maliev identity cookie scheme across BFFs.
/// Both Maliev.Web.Bff and Maliev.QuoteEngine.Bff call this to participate in the same
/// .maliev.com-scoped session. The cookie is __Secure-Maliev.Identity.
/// <para>
/// Data Protection key ring persistence (Redis) is the responsibility of each BFF:
/// call <c>builder.Services.AddDataProtection().PersistKeysToStackExchangeRedis(...)</c>
/// after this method, using the <see cref="DataProtectionApplicationName"/> constant.
/// </para>
/// </summary>
public static class IdentityCookieExtensions
{
    /// <summary>Name of the shared identity cookie issued by all Maliev BFFs.</summary>
    public const string IdentityCookieName = "__Secure-Maliev.Identity";

    /// <summary>Authentication scheme name for the short-lived external OAuth temp cookie.</summary>
    public const string ExternalSchemeName = "MalievExternal";

    /// <summary>
    /// Data Protection application name shared across all Maliev BFFs.
    /// Both BFFs must use this name and the same Redis key ring to decrypt each other's cookies.
    /// </summary>
    public const string DataProtectionApplicationName = "Maliev.Identity";

    /// <summary>Redis key under which Data Protection XML keys are persisted.</summary>
    public const string DataProtectionRedisKey = "DataProtection-Keys";

    /// <summary>
    /// Adds the shared identity cookie and sets the Data Protection application name.
    /// Reads <c>Auth:CookieDomain</c> from config (e.g. <c>.maliev.com</c> in prod; unset in dev).
    /// Returns <see cref="AuthenticationBuilder"/> so callers can chain additional schemes (e.g. Google OAuth).
    /// </summary>
    public static AuthenticationBuilder AddMalievIdentityCookie(
        this IHostApplicationBuilder builder,
        Action<CookieAuthenticationOptions>? configurePrimary = null)
    {
        // SetApplicationName ensures both BFFs derive protection keys from the same application name.
        // Redis key ring persistence is wired by each BFF so that this package stays out of ServiceDefaults.
        builder.Services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName);

        var cookieDomain = builder.Configuration["Auth:CookieDomain"];

        // The __Secure- prefix requires the Secure attribute on every Set-Cookie,
        // including the expired cookie written by sign-out. SameAsRequest omits it
        // over http (browsers then reject the deletion and the session survives
        // log out — observed on http://localhost). Browsers accept Secure cookies
        // from localhost, so Always is safe for dev; the in-process test host has
        // no TLS and no prefix enforcement, so tests keep SameAsRequest.
        var securePolicy = builder.Environment.IsEnvironment("Testing")
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        return builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = ExternalSchemeName;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = IdentityCookieName;
                options.Cookie.SecurePolicy = securePolicy;
                if (!string.IsNullOrWhiteSpace(cookieDomain))
                {
                    options.Cookie.Domain = cookieDomain;
                }
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                options.LoginPath = "/auth/sign-in";
                options.LogoutPath = "/auth/sign-out";
                options.AccessDeniedPath = "/auth/sign-in";
                configurePrimary?.Invoke(options);
            })
            .AddCookie(ExternalSchemeName, options =>
            {
                options.Cookie.Name = "__Secure-Maliev.Identity.External";
                options.Cookie.SecurePolicy = securePolicy;
                if (!string.IsNullOrWhiteSpace(cookieDomain))
                {
                    options.Cookie.Domain = cookieDomain;
                }
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });
    }
}
