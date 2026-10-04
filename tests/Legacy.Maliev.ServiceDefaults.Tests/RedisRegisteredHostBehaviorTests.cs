using System.Text;
using Maliev.Aspire.ServiceDefaults.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class RedisRegisteredHostBehaviorTests(RedisCacheRuntimeFixture fixture)
    : IClassFixture<RedisCacheRuntimeFixture>
{
    [Fact]
    public async Task StandardCache_ProductionResolvesRealStoresAndReadyHealthWithOwnedPrefix()
    {
        var builder = Builder("Production", fixture.ConnectionString);
        var prefix = Prefix();
        builder.AddStandardCache(prefix);
        using var host = builder.Build();
        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        try
        {
            using var scope = host.Services.CreateScope();
            var cache = Assert.IsType<RedisCacheService>(scope.ServiceProvider.GetRequiredService<ICacheService>());
            var distributed = Assert.IsAssignableFrom<RedisCache>(host.Services.GetRequiredService<IDistributedCache>());
            Assert.True(connection.IsConnected);
            var options = host.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
            Assert.Equal(prefix, options.InstanceName);
            var configured = ConfigurationOptions.Parse(options.Configuration!);
            Assert.Equal(10000, configured.SyncTimeout);
            Assert.Equal(10000, configured.AsyncTimeout);
            await cache.SetAsync("object", "ข้อความไทย", TimeSpan.FromMinutes(1));
            await distributed.SetStringAsync("distributed", "distributed-value");
            var database = fixture.Connection.GetDatabase();
            Assert.Equal("ข้อความไทย", (await database.StringGetAsync(prefix + "object")).ToString());
            Assert.Equal("distributed-value", Encoding.UTF8.GetString((byte[])(await database.HashGetAsync(prefix + "distributed", "data"))!));
            var health = await host.Services.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(registration => registration.Tags.Contains("ready"));
            Assert.Equal(HealthStatus.Healthy, health.Status);
            Assert.Equal(HealthStatus.Healthy, health.Entries["redis"].Status);
        }
        finally
        {
            // AddStandardCache registers a caller-created singleton instance; explicitly own it.
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task StandardCache_ClosingOwnedConnectionMakesReadyHealthUnhealthyWithoutClosingFixture()
    {
        var builder = Builder("Production", fixture.ConnectionString);
        builder.AddStandardCache(Prefix());
        using var host = builder.Build();
        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        try
        {
            var healthChecks = host.Services.GetRequiredService<HealthCheckService>();
            var before = await healthChecks.CheckHealthAsync(registration => registration.Tags.Contains("ready"));
            Assert.Equal(HealthStatus.Healthy, before.Entries["redis"].Status);

            await connection.CloseAsync();
            Assert.False(connection.IsConnected);
            var after = await healthChecks.CheckHealthAsync(registration => registration.Tags.Contains("ready"));
            Assert.Equal(HealthStatus.Unhealthy, after.Status);
            Assert.Equal(HealthStatus.Unhealthy, after.Entries["redis"].Status);
            Assert.True(fixture.Connection.IsConnected);
            Assert.True(await fixture.Connection.GetDatabase().PingAsync() >= TimeSpan.Zero);
        }
        finally { await connection.DisposeAsync(); }
    }

    [Fact]
    public async Task StandardCache_ConfiguredTimeoutsArePreservedWhileRealCacheRemainsUsable()
    {
        var builder = Builder("Production", fixture.ConnectionString + ",syncTimeout=1234,asyncTimeout=2345");
        builder.AddStandardCache(Prefix());
        using var host = builder.Build();
        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        try
        {
            var options = host.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
            var configured = ConfigurationOptions.Parse(options.Configuration!);
            Assert.Equal(1234, configured.SyncTimeout);
            Assert.Equal(2345, configured.AsyncTimeout);
            using var scope = host.Services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
            Assert.Equal(1L, await cache.IncrementAsync("counter", TimeSpan.FromMinutes(1)));
            Assert.Equal("1", await cache.GetAsync<string>("counter"));
        }
        finally { await connection.DisposeAsync(); }
    }

    [Fact]
    public async Task RedisExtensions_ExplicitOverloadRegistersActualCacheMultiplexerAndReadyCheck()
    {
        var builder = Builder("Production", fixture.ConnectionString);
        builder.Configuration["Redis:ConnectTimeout"] = "1234";
        builder.Configuration["Redis:SyncTimeout"] = "2345";
        builder.Configuration["Redis:AsyncTimeout"] = "3456";
        var prefix = Prefix();
        RedisExtensions.AddRedisDistributedCache(builder, prefix, options => options.ConnectRetry = 0);
        using var host = builder.Build();
        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        Assert.True(connection.IsConnected);
        var configured = host.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        Assert.Equal(prefix, configured.InstanceName);
        Assert.NotNull(configured.ConfigurationOptions);
        Assert.Equal(1234, configured.ConfigurationOptions.ConnectTimeout);
        Assert.Equal(2345, configured.ConfigurationOptions.SyncTimeout);
        Assert.Equal(3456, configured.ConfigurationOptions.AsyncTimeout);
        Assert.Equal(0, configured.ConfigurationOptions.ConnectRetry);
        var cache = Assert.IsType<RedisCacheService>(host.Services.GetRequiredService<ICacheService>());
        await cache.SetAsync("key", "registered", TimeSpan.FromMinutes(1));
        Assert.Equal("registered", (await fixture.Connection.GetDatabase().StringGetAsync(prefix + "key")).ToString());
        var distributed = Assert.IsAssignableFrom<RedisCache>(host.Services.GetRequiredService<IDistributedCache>());
        await distributed.SetStringAsync("distributed", "registered-distributed", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        });
        Assert.Equal("registered-distributed", await distributed.GetStringAsync("distributed"));
        Assert.Equal("registered-distributed", Encoding.UTF8.GetString((byte[])(await fixture.Connection.GetDatabase()
            .HashGetAsync(prefix + "distributed", "data"))!));
        await distributed.RemoveAsync("distributed");
        Assert.False(await fixture.Connection.GetDatabase().KeyExistsAsync(prefix + "distributed"));
        var health = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Tags.Contains("ready"));
        Assert.Equal(HealthStatus.Healthy, health.Entries["redis"].Status);
    }

    [Fact]
    public async Task CachingExtensions_ExplicitOverloadRegistersOnlyDistributedCacheAndKeepsTimeouts()
    {
        var builder = Builder("Production", fixture.ConnectionString + ",syncTimeout=1234");
        var prefix = Prefix();
        CachingExtensions.AddRedisDistributedCache(builder, prefix);
        using var host = builder.Build();
        Assert.Null(host.Services.GetService<ICacheService>());
        Assert.Null(host.Services.GetService<IConnectionMultiplexer>());
        var options = host.Services.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        var configured = ConfigurationOptions.Parse(options.Configuration!);
        Assert.Equal(1234, configured.SyncTimeout);
        Assert.Equal(10000, configured.AsyncTimeout);
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync("key", "ไทย", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        });
        Assert.Equal("ไทย", await cache.GetStringAsync("key"));
        Assert.Equal("ไทย", Encoding.UTF8.GetString((byte[])(await fixture.Connection.GetDatabase().HashGetAsync(prefix + "key", "data"))!));
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public void StandardCache_InvalidConfigurationFailsRequiredRedisWithoutNetworkRetry(string environment, bool allowFallback)
    {
        // A deterministic parser failure, never an unrelated service, failed port or timeout probe.
        var builder = Builder(environment, fixture.ConnectionString + ",fixtureUnsupportedOption=true");
        builder.Configuration["Cache:AllowInMemoryFallback"] = allowFallback.ToString();
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddStandardCache(Prefix()));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ICacheService));
    }

    [Fact]
    public async Task StandardCache_ExplicitDevelopmentFallbackAfterParserFailureStillProvidesUsableLocalCache()
    {
        var builder = Builder("Development", fixture.ConnectionString + ",fixtureUnsupportedOption=true");
        builder.Configuration["Cache:AllowInMemoryFallback"] = "true";
        builder.AddStandardCache(Prefix());
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var cache = Assert.IsType<InMemoryCacheService>(scope.ServiceProvider.GetRequiredService<ICacheService>());
        await cache.SetAsync("key", "fallback-value", TimeSpan.FromMinutes(1));
        Assert.Equal("fallback-value", await cache.GetAsync<string>("key"));
        Assert.IsType<MemoryDistributedCache>(host.Services.GetRequiredService<IDistributedCache>());
        Assert.Null(host.Services.GetService<IConnectionMultiplexer>());
    }

    [Fact]
    public async Task NamedMultiplexerRegistration_ResolvesNamedOwnedEndpointAndConfiguredOptions()
    {
        var builder = Builder("Production", null);
        builder.Configuration["ConnectionStrings:owned-cache"] = fixture.ConnectionString;
        builder.AddRedisConnectionMultiplexer("owned-cache", options =>
        {
            options.ConnectRetry = 0;
            options.ClientName = "fixture-registration";
        });
        using var host = builder.Build();
        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        Assert.True(connection.IsConnected);
        Assert.Equal("fixture-registration", connection.ClientName);
        Assert.Null(host.Services.GetService<ICacheService>());
        var key = Prefix() + "named";
        await connection.GetDatabase().StringSetAsync(key, "named-value");
        Assert.Equal("named-value", (await fixture.Connection.GetDatabase().StringGetAsync(key)).ToString());
    }

    private static HostApplicationBuilder Builder(string environment, string? connection)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = connection,
            ["Cache:RedisEnabled"] = "true"
        });
        return builder;
    }

    private static string Prefix() => "fixture:registered:" + Guid.NewGuid().ToString("N") + ":";
}
