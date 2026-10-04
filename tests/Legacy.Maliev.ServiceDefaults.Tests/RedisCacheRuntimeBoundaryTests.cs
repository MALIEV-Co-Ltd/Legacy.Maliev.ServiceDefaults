using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Caching;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class RedisCacheRuntimeBoundaryTests(RedisCacheRuntimeFixture fixture)
    : IClassFixture<RedisCacheRuntimeFixture>
{
    [Fact]
    public async Task SetAndGet_UnicodeObjectUsesPrefixedCamelCaseWireAndFiniteTtl()
    {
        var prefix = Prefix();
        var cache = Cache(fixture.Connection, prefix);
        var started = Stopwatch.GetTimestamp();
        await cache.SetAsync("ลูกค้า", new CachedValue("ช่างไทย", 17), TimeSpan.FromMinutes(1));

        var database = fixture.Connection.GetDatabase();
        var raw = await database.StringGetAsync(prefix + "ลูกค้า");
        using var json = JsonDocument.Parse(raw.ToString());
        Assert.Equal("ช่างไทย", json.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(17, json.RootElement.GetProperty("count").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("DisplayName", out _));
        Assert.False(await database.KeyExistsAsync("ลูกค้า"));
        Assert.Equal(new CachedValue("ช่างไทย", 17), await cache.GetAsync<CachedValue>("ลูกค้า"));
        Assert.True(await cache.ExistsAsync("ลูกค้า"));
        AssertFiniteTtl(await database.KeyTimeToLiveAsync(prefix + "ลูกค้า"), 60, started);
    }

    [Fact]
    public async Task StringValue_IsNotJsonQuotedAndMissingKeysRemainAbsent()
    {
        var prefix = Prefix();
        var cache = Cache(fixture.Connection, prefix);
        await cache.SetAsync("text", "ข้อความไทย\"literal", TimeSpan.FromMinutes(1));
        Assert.Equal("ข้อความไทย\"literal", (await fixture.Connection.GetDatabase().StringGetAsync(prefix + "text")).ToString());
        Assert.Equal("ข้อความไทย\"literal", await cache.GetAsync<string>("text"));
        Assert.Null(await cache.GetAsync<CachedValue>("missing"));
        Assert.False(await cache.ExistsAsync("missing"));
    }

    [Fact]
    public async Task IndependentlySeededPascalCaseJson_PreservesCaseInsensitiveUnicodeReads()
    {
        var prefix = Prefix();
        await fixture.Connection.GetDatabase().StringSetAsync(prefix + "legacy-object",
            "{\"DisplayName\":\"ช่างไทย\",\"Count\":17}");

        Assert.Equal(new CachedValue("ช่างไทย", 17),
            await Cache(fixture.Connection, prefix).GetAsync<CachedValue>("legacy-object"));
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheSelectedNamespaceAndIsIdempotent()
    {
        var prefix = Prefix();
        var cache = Cache(fixture.Connection, prefix);
        var database = fixture.Connection.GetDatabase();
        await cache.SetAsync("same-key", "owned", TimeSpan.FromMinutes(1));
        await database.StringSetAsync(prefix + "other:same-key", "retained");
        await cache.RemoveAsync("same-key");
        await cache.RemoveAsync("same-key");
        Assert.False(await database.KeyExistsAsync(prefix + "same-key"));
        Assert.Equal("retained", (await database.StringGetAsync(prefix + "other:same-key")).ToString());
    }

    [Fact]
    public async Task RemoveByPattern_UsesRedisGlobWithinOwnedPrefixIncludingUnicode()
    {
        var prefix = Prefix();
        var cache = Cache(fixture.Connection, prefix);
        var database = fixture.Connection.GetDatabase();
        await cache.SetAsync("ลูกค้า:1", "first", TimeSpan.FromMinutes(1));
        await cache.SetAsync("ลูกค้า:2", "second", TimeSpan.FromMinutes(1));
        await cache.SetAsync("ใบเสร็จ:1", "retained", TimeSpan.FromMinutes(1));
        await database.StringSetAsync(prefix + "other:ลูกค้า:1", "other-namespace");
        await cache.RemoveByPatternAsync("ลูกค้า:*");
        Assert.False(await database.KeyExistsAsync(prefix + "ลูกค้า:1"));
        Assert.False(await database.KeyExistsAsync(prefix + "ลูกค้า:2"));
        Assert.Equal("retained", await cache.GetAsync<string>("ใบเสร็จ:1"));
        Assert.Equal("other-namespace", (await database.StringGetAsync(prefix + "other:ลูกค้า:1")).ToString());
    }

    [Fact]
    public async Task Increment_ConcurrentCallersProduceEveryCounterValueOnceWithInitialTtl()
    {
        var prefix = Prefix();
        var cache = Cache(fixture.Connection, prefix);
        var started = Stopwatch.GetTimestamp();
        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => cache.IncrementAsync("counter", TimeSpan.FromMinutes(1))));
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (long)value), results.Order());
        Assert.Equal("32", (await fixture.Connection.GetDatabase().StringGetAsync(prefix + "counter")).ToString());
        AssertFiniteTtl(await fixture.Connection.GetDatabase().KeyTimeToLiveAsync(prefix + "counter"), 60, started);
    }

    [Fact]
    public async Task Increment_ExistingCounterDoesNotExtendItsExpiration()
    {
        var prefix = Prefix();
        var database = fixture.Connection.GetDatabase();
        var started = Stopwatch.GetTimestamp();
        await database.StringSetAsync(prefix + "counter", "8", TimeSpan.FromSeconds(30));
        Assert.Equal(9L, await Cache(fixture.Connection, prefix).IncrementAsync("counter", TimeSpan.FromMinutes(5)));
        AssertFiniteTtl(await database.KeyTimeToLiveAsync(prefix + "counter"), 30, started);
    }

    [Fact]
    public async Task MalformedStoredJson_ReturnsMissWithPrivateExceptionTypeNotPayload()
    {
        var prefix = Prefix();
        var logger = new PrivateRedisLogger();
        await fixture.Connection.GetDatabase().StringSetAsync(prefix + "private-key-sentinel", "private-payload-sentinel");
        Assert.Null(await Cache(fixture.Connection, prefix, logger).GetAsync<CachedValue>("private-key-sentinel"));
        AssertPrivateFailure(logger, "System.Text.Json.JsonException");
    }

    [Fact]
    public async Task Increment_NonNumericStoredValueReturnsZeroWithoutChangingValue()
    {
        var prefix = Prefix();
        var logger = new PrivateRedisLogger();
        var database = fixture.Connection.GetDatabase();
        await database.StringSetAsync(prefix + "private-key-sentinel", "private-payload-sentinel");
        Assert.Equal(0L, await Cache(fixture.Connection, prefix, logger).IncrementAsync("private-key-sentinel", TimeSpan.FromMinutes(1)));
        Assert.Equal("private-payload-sentinel", (await database.StringGetAsync(prefix + "private-key-sentinel")).ToString());
        AssertPrivateFailure(logger, "StackExchange.Redis.RedisServerException");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("remove")]
    [InlineData("exists")]
    [InlineData("increment")]
    public async Task DisposedOwnedConnection_PreservesFallbackWithPrivateDiagnostic(string operation)
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var logger = new PrivateRedisLogger();
        var cache = Cache(connection, Prefix(), logger);
        await connection.DisposeAsync();
        switch (operation)
        {
            case "get": Assert.Null(await cache.GetAsync<CachedValue>("private-key-sentinel")); break;
            case "set": await cache.SetAsync("private-key-sentinel", "private-payload-sentinel", TimeSpan.FromMinutes(1)); break;
            case "remove": await cache.RemoveAsync("private-key-sentinel"); break;
            case "exists": Assert.False(await cache.ExistsAsync("private-key-sentinel")); break;
            case "increment": Assert.Equal(0L, await cache.IncrementAsync("private-key-sentinel", TimeSpan.FromMinutes(1))); break;
            default: throw new ArgumentException("Unknown fixture operation.", nameof(operation));
        }
        AssertPrivateFailure(logger, "System.ObjectDisposedException");
    }

    [Fact]
    public async Task ClosedOwnedConnection_PatternRemovalIsNonThrowingAndDoesNotDeleteData()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var prefix = Prefix();
        var logger = new PrivateRedisLogger();
        await fixture.Connection.GetDatabase().StringSetAsync(prefix + "private-key-sentinel", "retained");
        var cache = Cache(connection, prefix, logger);
        await connection.CloseAsync();
        Assert.False(connection.IsConnected);
        await cache.RemoveByPatternAsync("private-key-sentinel*");
        Assert.Equal("retained", (await fixture.Connection.GetDatabase().StringGetAsync(prefix + "private-key-sentinel")).ToString());
        var jsonText = Assert.Single(logger.Entries);
        using var json = JsonDocument.Parse(jsonText);
        Assert.Equal("WARNING", json.RootElement.GetProperty("severity").GetString());
        Assert.DoesNotContain("private-key-sentinel", jsonText, StringComparison.Ordinal);
    }

    private static string Prefix() => "fixture:" + Guid.NewGuid().ToString("N") + ":";
    private static RedisCacheService Cache(IConnectionMultiplexer connection, string prefix, PrivateRedisLogger? logger = null) =>
        new(connection, Options.Create(new RedisCacheOptions { InstanceName = prefix }), logger ?? new PrivateRedisLogger());

    private static void AssertFiniteTtl(TimeSpan? ttl, double expectedSeconds, long started)
    {
        var actualTtl = Assert.NotNull(ttl);
        var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Assert.True(elapsedSeconds < expectedSeconds - 1d,
            "The fixture must observe TTL before its bounded expiry window ends.");
        // Account for measured operation time and Redis expiry precision, not an
        // arbitrary positive TTL that could hide an incorrectly shortened expiry.
        Assert.InRange(actualTtl.TotalSeconds, expectedSeconds - elapsedSeconds - 1d, expectedSeconds);
    }

    private static void AssertPrivateFailure(PrivateRedisLogger logger, string type)
    {
        var jsonText = Assert.Single(logger.Entries);
        using var json = JsonDocument.Parse(jsonText);
        Assert.Equal("WARNING", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal(type, json.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-key-sentinel", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-payload-sentinel", jsonText, StringComparison.Ordinal);
    }

    public sealed record CachedValue(string DisplayName, int Count);

    private sealed class PrivateRedisLogger : ILogger<RedisCacheService>
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var entry = new LogEntry<TState>(logLevel, typeof(RedisCacheService).FullName!, eventId, state, exception, formatter);
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(in entry, null, writer);
            Entries.Enqueue(writer.ToString());
        }
    }
}

public sealed class RedisCacheRuntimeFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4.5-alpine").Build();
    public ConnectionMultiplexer Connection { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        }
        catch
        {
            await _container.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try { if (Connection is not null) await Connection.DisposeAsync(); }
        finally { await _container.DisposeAsync(); }
    }
}
