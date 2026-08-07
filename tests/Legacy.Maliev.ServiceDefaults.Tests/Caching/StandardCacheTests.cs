using Maliev.Aspire.ServiceDefaults.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.ServiceDefaults.Tests.Caching;

public sealed class StandardCacheTests
{
    [Fact]
    public void Production_defaults_to_fail_closed_when_redis_is_unavailable()
    {
        var builder = CreateBuilder("Production", allowFallback: null);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddStandardCache("legacy:test:"));

        Assert.Contains("Redis is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_ignores_in_memory_fallback_override()
    {
        var builder = CreateBuilder("Production", allowFallback: true);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddStandardCache("legacy:test:"));

        Assert.Contains("Redis is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_allows_explicit_in_memory_fallback()
    {
        var builder = CreateBuilder("Development", allowFallback: true);

        builder.AddStandardCache("legacy:test:");
        using var provider = builder.Services.BuildServiceProvider();

        Assert.IsType<Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache>(
            provider.GetRequiredService<IDistributedCache>());
        Assert.IsType<InMemoryCacheService>(provider.GetRequiredService<ICacheService>());
    }

    [Fact]
    public async Task Redis_health_check_reports_connection_state_and_honors_cancellation()
    {
        var available = true;
        var check = new RedisAvailabilityHealthCheck(() => available);
        var context = new HealthCheckContext();

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(context)).Status);
        available = false;
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(context)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => check.CheckHealthAsync(context, cancellation.Token));
    }

    private static IHostApplicationBuilder CreateBuilder(string environmentName, bool? allowFallback)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName,
        });
        var values = new Dictionary<string, string?>
        {
            ["Cache:RedisEnabled"] = "true",
            ["ConnectionStrings:redis"] = "127.0.0.1:1,abortConnect=true,connectTimeout=50,connectRetry=0",
        };
        if (allowFallback is not null)
        {
            values["Cache:AllowInMemoryFallback"] = allowFallback.Value.ToString();
        }

        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }
}
