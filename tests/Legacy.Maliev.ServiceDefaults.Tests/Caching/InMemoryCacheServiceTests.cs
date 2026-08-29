using System.Reflection;
using Maliev.Aspire.ServiceDefaults.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ServiceDefaults.Tests.Caching;

public sealed class InMemoryCacheServiceTests
{
    [Fact]
    public async Task Expired_entries_are_removed_from_the_pattern_index()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new InMemoryCacheService(memory, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("customer:1", new TestCacheValue("value"), TimeSpan.FromMilliseconds(25));
        Assert.Equal(1, GetTrackedKeyCount(service));

        await WaitUntilAsync(
            async () => await service.GetAsync<TestCacheValue>("customer:1") is null,
            TimeSpan.FromSeconds(1));

        Assert.Equal(0, GetTrackedKeyCount(service));
    }

    [Fact]
    public async Task Expired_increment_entries_are_removed_from_the_pattern_index()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new InMemoryCacheService(memory, NullLogger<InMemoryCacheService>.Instance);

        Assert.Equal(1, await service.IncrementAsync("counter:1", TimeSpan.FromMilliseconds(25)));
        Assert.Equal(1, GetTrackedKeyCount(service));

        await WaitUntilAsync(
            async () => !await service.ExistsAsync("counter:1"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(0, GetTrackedKeyCount(service));
    }

    [Fact]
    public async Task Pattern_removal_only_removes_matching_active_entries()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new InMemoryCacheService(memory, NullLogger<InMemoryCacheService>.Instance);

        await service.SetAsync("customer:1", new TestCacheValue("one"), TimeSpan.FromMinutes(1));
        await service.SetAsync("customer:2", new TestCacheValue("two"), TimeSpan.FromMinutes(1));
        await service.SetAsync("order:1", new TestCacheValue("order"), TimeSpan.FromMinutes(1));

        await service.RemoveByPatternAsync("customer:*");

        Assert.Null(await service.GetAsync<TestCacheValue>("customer:1"));
        Assert.Null(await service.GetAsync<TestCacheValue>("customer:2"));
        Assert.Equal("order", (await service.GetAsync<TestCacheValue>("order:1"))?.Value);
    }

    [Fact]
    public void Development_fallback_uses_a_bounded_entry_unit_limit_for_the_local_cache()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:RedisEnabled"] = "false",
        });

        builder.AddStandardCache("legacy:test:");
        using var provider = builder.Services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MemoryCacheOptions>>().Value;
        var distributedOptions = provider.GetRequiredService<IOptions<MemoryDistributedCacheOptions>>().Value;

        Assert.Equal(25_000, options.SizeLimit);
        Assert.Equal(50 * 1024 * 1024, distributedOptions.SizeLimit);
    }

    private static int GetTrackedKeyCount(InMemoryCacheService service)
    {
        var field = typeof(InMemoryCacheService).GetField("_keys", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var keys = field!.GetValue(service);
        Assert.NotNull(keys);

        var count = keys!.GetType().GetProperty("Count")?.GetValue(keys);
        return Assert.IsType<int>(count);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The cache entry did not expire before the timeout.");
    }

    private sealed record TestCacheValue(string Value);
}
