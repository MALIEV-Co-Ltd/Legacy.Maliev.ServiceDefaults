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
}
