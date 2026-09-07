using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class DefaultEndpointsBehaviorTests
{
    [Fact]
    public void MapDefaultEndpoints_RejectsMissingServicePrefix()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapDefaultEndpoints(" "));
    }

    [Fact]
    public async Task DefaultEndpoints_ExposeAnonymousHealthAndSanitizedReadiness()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(DefaultEndpointsBehaviorTests).Assembly.GetName().Name
        });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
        builder.Configuration["Observability:TracingEnabled"] = "true";
        builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();
        builder.Services.AddHealthChecks().AddCheck(
            "controlled-dependency",
            () => HealthCheckResult.Unhealthy("private infrastructure detail"));

        await using var app = builder.Build();
        app.MapDefaultEndpoints("review");
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal("Healthy", await client.GetStringAsync("/review/aspire-liveness"));
        Assert.Equal("Healthy", await client.GetStringAsync("/review/liveness"));

        using var readiness = await client.GetAsync("/review/readiness");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("application/json", readiness.Content.Headers.ContentType?.MediaType);
        var body = await readiness.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private infrastructure detail", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("Unhealthy", document.RootElement.GetProperty("status").GetString());
        var check = document.RootElement.GetProperty("checks").GetProperty("controlled-dependency");
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.True(check.GetProperty("duration").GetDouble() >= 0);
        Assert.True(document.RootElement.GetProperty("totalDuration").GetDouble() >= 0);

        using var metrics = await client.GetAsync("/review/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
    }
}
