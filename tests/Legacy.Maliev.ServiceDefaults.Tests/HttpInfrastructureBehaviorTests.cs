using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class HttpInfrastructureBehaviorTests
{
    [Theory]
    [InlineData("array")]
    [InlineData("comma")]
    [InlineData("double-underscore")]
    [InlineData("legacy")]
    [InlineData("development")]
    [InlineData("default")]
    [InlineData("named")]
    [InlineData("custom")]
    public async Task Cors_OnlyAllowsConfiguredOrigin_OnActualPreflight(string mode)
    {
        var builder = Builder(mode == "development" ? Environments.Development : Environments.Production);
        var origin = mode is "development" or "default" or "named" ? "http://localhost:3000" : "https://allowed.example";
        switch (mode)
        {
            case "array": builder.Configuration["CORS:AllowedOrigins:0"] = origin; builder.AddStandardCors(); break;
            case "comma": builder.Configuration["CORS:AllowedOrigins"] = origin + ", https://second.example"; builder.AddStandardCors(); break;
            case "double-underscore": builder.Configuration["CORS__AllowedOrigins"] = origin; builder.AddStandardCors(); break;
            case "legacy": builder.Configuration["CORS_ALLOWED_ORIGINS"] = origin; builder.AddStandardCors(); break;
            case "development": builder.AddStandardCors(); break;
            case "default": builder.AddDefaultCors(); break;
            case "named": builder.AddDefaultCors("reviewed"); break;
            case "custom": builder.AddCorsWithOptions(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod().AllowCredentials())); break;
        }
        await using var app = builder.Build();
        if (mode == "named") app.UseCors("reviewed"); else app.UseCors();
        app.Run(context => context.Response.WriteAsync("ok"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var candidate in new[] { origin, "https://forbidden.example" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/resource");
            request.Headers.Add("Origin", candidate);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "X-Review");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            if (candidate == origin)
            {
                Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            }
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public void Cors_MissingProductionOrigins_FailsBeforeHostStarts()
    {
        var builder = Builder(Environments.Production);
        Assert.Throws<InvalidOperationException>(() => builder.AddStandardCors());
    }

    [Theory]
    [InlineData("default", 20)]
    [InlineData("api", 20)]
    [InlineData("public", 10)]
    [InlineData("admin", 5)]
    [InlineData("batch", 2)]
    [InlineData("auth", 1)]
    [InlineData("read", 40)]
    [InlineData("write", 10)]
    [InlineData("admin-endpoints", 5)]
    [InlineData("PublicApi", 10)]
    [InlineData("AuthenticatedApi", 20)]
    [InlineData("token_limit", 1)]
    [InlineData("ContactPolicy", 10)]
    [InlineData("GlobalPolicy", 20)]
    [InlineData("general", 20)]
    [InlineData("anonymous", 10)]
    public async Task RateLimit_NamedPolicy_EnforcesItsOwnBudgetAndJsonRejection(string policy, int permits)
    {
        var builder = Builder(Environments.Production);
        builder.AddStandardRateLimiting(options => { options.PermitLimit = 20; options.WindowMinutes = 10; options.QueueLimit = 0; });
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/limited", () => "ok").RequireRateLimiting(policy);
        await app.StartAsync();
        using var client = app.GetTestClient();
        for (var index = 0; index < permits; index++)
        {
            using var accepted = await client.GetAsync("/limited");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        using var rejected = await client.GetAsync("/limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("Too many requests", body.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("retryAfter").GetString()));
    }

    [Fact]
    public async Task RateLimit_GlobalBudget_IsSharedWithinHostAndSeparatedAcrossHosts()
    {
        var builder = Builder(Environments.Production);
        builder.Configuration["RateLimiting:PermitLimit"] = "2";
        builder.Configuration["RateLimiting:UseGlobalLimiter"] = "true";
        builder.AddStandardRateLimiting();
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.Run(context => context.Response.WriteAsync("ok"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://first.example/a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://first.example/b")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("http://first.example/c")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://second.example/a")).StatusCode);
    }

    private static WebApplicationBuilder Builder(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseTestServer();
        return builder;
    }
}
