using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class NativeJsonLoggingTests
{
    [Fact]
    public void Defaults_ConfigureScopedUtcJsonAndActivityCorrelation()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.Tags);
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var console = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        var json = host.Services.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;
        var logging = host.Services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value;

        Assert.Equal("maliev-cloud-json", console.FormatterName);
        Assert.True(json.IncludeScopes);
        Assert.True(json.UseUtcTimestamp);
        Assert.Equal("O", json.TimestampFormat);
        Assert.Equal(ActivityTrackingOptions.Tags | ActivityTrackingOptions.TraceId
            | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId, logging.ActivityTrackingOptions);
    }

    [Fact]
    public void Defaults_PreserveExistingProvidersFiltersAndOpenTelemetry()
    {
        var builder = Host.CreateApplicationBuilder();
        var existingProvider = new ExistingProvider();
        builder.Logging.AddProvider(existingProvider);
        builder.Logging.AddFilter("Customer.ControlledCategory", LogLevel.Error);
        builder.AddServiceDefaults();
        using var host = builder.Build();

        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
        Assert.Contains(existingProvider, providers);
        Assert.Single(providers.OfType<ConsoleLoggerProvider>());
        Assert.Single(providers.OfType<OpenTelemetryLoggerProvider>());
        var filters = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Contains(filters.Rules, rule => rule.CategoryName == "Customer.ControlledCategory"
            && rule.LogLevel == LogLevel.Error);
        Assert.Contains(filters.Rules, rule => rule.CategoryName == "Polly"
            && rule.LogLevel == LogLevel.Error);
        var telemetry = host.Services.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>().Value;
        // Framework hosting scopes include literal request paths; OTLP must not export them.
        Assert.False(telemetry.IncludeScopes);
        Assert.True(telemetry.IncludeFormattedMessage);
    }

    private sealed class ExistingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }
    }
}
