using System.Net;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class OpenApiEndpointBehaviorTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", false)]
    public async Task Documentation_ExposesVersionedSchemaOnlyOutsideProduction(string environment, bool exposed)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseTestServer();
        builder.AddStandardOpenApi("Review API", "Controlled documentation");
        await using var app = builder.Build();
        var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1)).Build();
        app.MapGet("/items", () => new ReviewItem(7, "sample")).WithApiVersionSet(versionSet).MapToApiVersion(1);
        app.MapApiDocumentation("review");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var schema = await client.GetAsync("/review/openapi/v1.json");
        Assert.Equal(exposed ? HttpStatusCode.OK : HttpStatusCode.NotFound, schema.StatusCode);
        if (exposed)
        {
            using var document = JsonDocument.Parse(await schema.Content.ReadAsStringAsync());
            Assert.Equal("Review API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
            Assert.Equal("Controlled documentation", document.RootElement.GetProperty("info").GetProperty("description").GetString());
            Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/items", out _));
            using var ui = await client.GetAsync("/review/scalar");
            Assert.Equal(HttpStatusCode.Found, ui.StatusCode);
            Assert.NotNull(ui.Headers.Location);
            using var documentUi = await client.GetAsync(new Uri(ui.RequestMessage!.RequestUri!, ui.Headers.Location));
            Assert.Equal(HttpStatusCode.OK, documentUi.StatusCode);
            var html = await documentUi.Content.ReadAsStringAsync();
            Assert.Contains("\"url\":\"review/openapi/v1.json\"", html, StringComparison.Ordinal);
            Assert.Contains("Review Documentation", html, StringComparison.Ordinal);
        }
    }

    public sealed record ReviewItem(int Id, string Name);

    [Theory]
    [InlineData("Development", "v1", true)]
    [InlineData("Development", "v2", true)]
    [InlineData("Staging", "v1", true)]
    [InlineData("Staging", "v2", true)]
    [InlineData("Production", "v1", false)]
    [InlineData("Production", "v2", false)]
    public async Task Standard_versioned_documents_expose_shared_XML_schema_descriptions(
        string environment, string documentName, bool exposed)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseTestServer();
        builder.AddStandardOpenApi("Review API", "Controlled documentation");
        await using var app = builder.Build();
        var versions = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1))
            .HasApiVersion(new ApiVersion(2)).Build();
        // These are fixture-only schema specimens, never live configuration or DI-resolved settings.
        app.MapGet("/specimens/middleware", () => new MiddlewareOptions())
            .WithApiVersionSet(versions).MapToApiVersion(1).MapToApiVersion(2);
        app.MapGet("/specimens/limiter", () => new Microsoft.Extensions.Hosting.RateLimiterOptions())
            .WithApiVersionSet(versions).MapToApiVersion(1).MapToApiVersion(2);
        app.MapApiDocumentation("review");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/review/openapi/{documentName}.json");
        Assert.Equal(exposed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (!exposed) return;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Review API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("Controlled documentation", root.GetProperty("info").GetProperty("description").GetString());
        Assert.Equal(documentName[1..], root.GetProperty("info").GetProperty("version").GetString());
        var middleware = ResolveResponseSchema(root, "/specimens/middleware");
        AssertDescription(middleware, "Configuration options for standard middleware components.");
        AssertDescription(middleware.GetProperty("properties").GetProperty("correlationIdHeaderName"),
            "Correlation ID header name.");
        AssertDescription(middleware.GetProperty("properties").GetProperty("enableRequestLogging"),
            "Enable request/response logging");
        var limiter = ResolveResponseSchema(root, "/specimens/limiter");
        AssertDescription(limiter, "Configuration options for rate limiting behavior.");
        AssertDescription(limiter.GetProperty("properties").GetProperty("permitLimit"),
            "maximum number of requests permitted per time window");
        AssertDescription(limiter.GetProperty("properties").GetProperty("windowMinutes"),
            "duration of the rate limiting time window in minutes");
    }

    private static JsonElement ResolveResponseSchema(JsonElement document, string path)
    {
        var schema = document.GetProperty("paths").GetProperty(path).GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var reference = schema.GetProperty("$ref").GetString();
        Assert.NotNull(reference);
        Assert.StartsWith("#/components/schemas/", reference, StringComparison.Ordinal);
        return document.GetProperty("components").GetProperty("schemas")
            .GetProperty(reference["#/components/schemas/".Length..]);
    }

    private static void AssertDescription(JsonElement schema, string expected)
    {
        Assert.True(schema.TryGetProperty("description", out var description),
            $"The shared OpenAPI registration must expose XML description: {expected}");
        Assert.Contains(expected, description.GetString(), StringComparison.Ordinal);
    }
}
