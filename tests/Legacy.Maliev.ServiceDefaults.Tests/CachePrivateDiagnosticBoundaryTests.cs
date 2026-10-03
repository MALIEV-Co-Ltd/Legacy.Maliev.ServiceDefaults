using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Caching;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class CachePrivateDiagnosticBoundaryTests
{
    [Theory]
    [InlineData("get", "WARNING")]
    [InlineData("set", "WARNING")]
    [InlineData("remove", "WARNING")]
    [InlineData("pattern", "ERROR")]
    [InlineData("exists", "WARNING")]
    [InlineData("increment", "WARNING")]
    public async Task DisposedLocalStore_PreservesFallbackAndOnlyLogsHashedKeys(string operation, string severity)
    {
        const string key = "fixture-private-session-key";
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var logger = new PrivateLogger();
        var cache = new InMemoryCacheService(memory, logger);
        await cache.SetAsync(key, "fixture-value", TimeSpan.FromMinutes(1));
        Assert.Empty(logger.Entries);
        memory.Dispose(); // Real storage lifecycle failure, not a substitute cache implementation.

        switch (operation)
        {
            case "get": Assert.Null(await cache.GetAsync<string>(key)); break;
            case "set": await cache.SetAsync(key, "fixture-new-value", TimeSpan.FromMinutes(1)); break;
            case "remove": await cache.RemoveAsync(key); break;
            case "pattern": await cache.RemoveByPatternAsync(key); break;
            case "exists": Assert.False(await cache.ExistsAsync(key)); break;
            case "increment": Assert.Equal(0L, await cache.IncrementAsync(key, TimeSpan.FromMinutes(1))); break;
            default: throw new ArgumentException("Unknown fixture operation.", nameof(operation));
        }

        var entry = Assert.Single(logger.Entries);
        var expectedHash = $"{key.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8)).ToLowerInvariant()}";
        Assert.Equal(expectedHash, Assert.Single(entry.Fields, field => field.Key ==
            (operation == "pattern" ? "PatternHash" : "KeyHash")).Value);
        Assert.All(entry.Fields, field =>
        {
            if (field.Value is not string value) return;
            Assert.DoesNotContain(key, value, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-value", value, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-new-value", value, StringComparison.Ordinal);
        });
        using var json = JsonDocument.Parse(entry.Json);
        Assert.Equal(severity, json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("System.ObjectDisposedException", json.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain(key, entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-value", entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-new-value", entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot access a disposed object", entry.Json, StringComparison.Ordinal);
    }

    private sealed record PrivateEntry(string Json, KeyValuePair<string, object?>[] Fields);

    private sealed class PrivateLogger : ILogger<InMemoryCacheService>
    {
        public List<PrivateEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            using var writer = new StringWriter();
            var entry = new LogEntry<TState>(logLevel, typeof(InMemoryCacheService).FullName!, eventId, state, exception, formatter);
            new PrivateFailureConsoleFormatter().Write(in entry, null, writer);
            Entries.Add(new PrivateEntry(writer.ToString(), state is IEnumerable<KeyValuePair<string, object?>> fields
                ? fields.ToArray() : []));
        }
    }
}
