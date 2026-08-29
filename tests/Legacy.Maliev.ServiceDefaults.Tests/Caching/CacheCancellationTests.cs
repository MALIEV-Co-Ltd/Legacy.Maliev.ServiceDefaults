using System.Reflection;
using Maliev.Aspire.ServiceDefaults.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Legacy.Maliev.ServiceDefaults.Tests.Caching;

public sealed class CacheCancellationTests
{
    [Fact]
    public async Task Redis_operations_honor_an_already_canceled_request_without_touching_redis()
    {
        var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, ThrowingConnectionMultiplexer>();
        var service = new RedisCacheService(
            multiplexer,
            Options.Create(new RedisCacheOptions { InstanceName = "legacy:test:" }),
            NullLogger<RedisCacheService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertCanceledAsync(token => service.GetAsync<TestCacheValue>("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.SetAsync("key", new TestCacheValue("value"), TimeSpan.FromMinutes(1), token), cancellation.Token);
        await AssertCanceledAsync(token => service.RemoveAsync("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.RemoveByPatternAsync("key:*", token), cancellation.Token);
        await AssertCanceledAsync(token => service.ExistsAsync("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.IncrementAsync("key", TimeSpan.FromMinutes(1), token), cancellation.Token);
    }

    [Fact]
    public async Task In_memory_operations_return_canceled_tasks_for_an_already_canceled_request()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = new InMemoryCacheService(memory, NullLogger<InMemoryCacheService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertCanceledAsync(token => service.GetAsync<TestCacheValue>("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.SetAsync("key", new TestCacheValue("value"), TimeSpan.FromMinutes(1), token), cancellation.Token);
        await AssertCanceledAsync(token => service.RemoveAsync("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.RemoveByPatternAsync("key:*", token), cancellation.Token);
        await AssertCanceledAsync(token => service.ExistsAsync("key", token), cancellation.Token);
        await AssertCanceledAsync(token => service.IncrementAsync("key", TimeSpan.FromMinutes(1), token), cancellation.Token);
    }

    private static async Task AssertCanceledAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(cancellationToken));
    }

    private sealed record TestCacheValue(string Value);

    private class ThrowingConnectionMultiplexer : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Redis should not be touched after cancellation.");
    }
}
