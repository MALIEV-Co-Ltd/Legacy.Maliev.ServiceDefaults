using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Console.Out is process-wide. These tests must not overlap any other collection.
[CollectionDefinition("Native console output", DisableParallelization = true)]
public sealed class NativeConsoleOutputCollection;

[Collection("Native console output")]
public sealed class NativeLoggingWireTests
{
    [Fact]
    public void Defaults_EmitActualScopedUtcJson_AndDeliverToExistingProvider()
    {
        var original = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var existing = new CapturingProvider();
        string traceId;
        string spanId;
        try
        {
            Console.SetOut(output);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.Logging.AddProvider(existing);
            builder.Logging.AddFilter("Wire.Filtered", LogLevel.Error);
            builder.AddServiceDefaults();
            using (var host = builder.Build())
            using (var activity = new Activity("controlled-operation").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                traceId = activity.TraceId.ToString();
                spanId = activity.SpanId.ToString();
                var factory = (ILoggerFactory)host.Services.GetService(typeof(ILoggerFactory))!;
                var logger = factory.CreateLogger("Wire.Native");
                using (logger.BeginScope(new Dictionary<string, object?> { ["ProofScope"] = "bounded-proof" }))
                {
                    logger.LogCritical("Controlled event {Sequence}", 7);
                }
                factory.CreateLogger("Wire.Filtered").LogInformation("Suppressed event");
            } // Disposing the host drains the console logger queue before restoring Console.Out.
        }
        finally
        {
            Console.SetOut(original);
        }

        var entry = Assert.Single(ReadEntries(output.ToString()), value => value.GetProperty("Category").GetString() == "Wire.Native");
        Assert.Equal("Critical", entry.GetProperty("LogLevel").GetString());
        Assert.Equal("CRITICAL", entry.GetProperty("severity").GetString());
        Assert.Equal("Controlled event 7", entry.GetProperty("Message").GetString());
        AssertUtc(entry.GetProperty("Timestamp").GetString()!);
        var scopes = entry.GetProperty("Scopes").EnumerateArray().ToArray();
        Assert.Contains(scopes, scope => scope.TryGetProperty("TraceId", out var value) && value.GetString() == traceId);
        Assert.Contains(scopes, scope => scope.TryGetProperty("SpanId", out var value) && value.GetString() == spanId);
        Assert.Contains(scopes, scope => scope.TryGetProperty("ProofScope", out var value) && value.GetString() == "bounded-proof");
        var captured = Assert.Single(existing.Entries, value => value.Category == "Wire.Native");
        Assert.Equal(LogLevel.Critical, captured.Level);
        Assert.Equal("Controlled event 7", captured.Message);
        Assert.DoesNotContain(existing.Entries, value => value.Message == "Suppressed event");
        Assert.DoesNotContain("Suppressed event", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_MapAllCloudSeveritiesWithoutSerializingExceptionText()
    {
        var original = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.AddServiceDefaults();
            using var host = builder.Build();
            var logger = ((ILoggerFactory)host.Services.GetService(typeof(ILoggerFactory))!).CreateLogger("Wire.Severity");
            logger.LogTrace("Trace event");
            logger.LogDebug("Debug event");
            logger.LogInformation("Info event");
            logger.LogWarning("Warning event");
            logger.LogError(new InvalidOperationException("private-exception-detail"), "Safe error event");
            logger.LogCritical("Critical event");
        }
        finally
        {
            Console.SetOut(original);
        }

        var events = ReadEntries(output.ToString())
            .Where(value => value.GetProperty("Category").GetString() == "Wire.Severity")
            .ToArray();
        Assert.Equal(6, events.Length);
        Assert.Equal(
            ["DEBUG", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"],
            events.Select(value => value.GetProperty("severity").GetString() ?? string.Empty).ToArray());
        Assert.All(events, value => AssertUtc(value.GetProperty("Timestamp").GetString()!));
        Assert.DoesNotContain("private-exception-detail", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.InternalServerError, "An internal server error occurred")]
    [InlineData(true, HttpStatusCode.BadRequest, "The request is invalid.")]
    public async Task StandardPipeline_ControlledHttpFailure_EmitsOneCorrelatedRedactedEvent(bool validation, HttpStatusCode status, string message)
    {
        var original = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var capture = new CapturingProvider();
        string body;
        string correlation;
        try
        {
            Console.SetOut(output);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ApplicationName = typeof(NativeLoggingWireTests).Assembly.GetName().Name
            });
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.WebHost.UseTestServer();
            builder.Logging.AddProvider(capture);
            builder.AddServiceDefaults();
            builder.AddStandardMiddleware();
            await using (var app = builder.Build())
            {
                app.UseStandardMiddleware();
                app.Run(_ => throw (validation ? new ArgumentException("private-exception") : new Exception("private-exception")));
                await app.StartAsync();
                using var client = app.GetTestClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/controlled?token=private-query");
                request.Headers.Add("Authorization", "Bearer private-authorization");
                request.Headers.Add("Cookie", "session=private-cookie");
                request.Headers.Add("X-Correlation-ID", "wire-correlation");
                using var response = await client.SendAsync(request);
                Assert.Equal(status, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                correlation = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
                body = await response.Content.ReadAsStringAsync();
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal("wire-correlation", correlation);
        using var responseJson = JsonDocument.Parse(body);
        Assert.Equal((int)status, responseJson.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(message, responseJson.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, responseJson.RootElement.GetProperty("details").ValueKind);
        var critical = Assert.Single(ReadEntries(output.ToString()), value => value.GetProperty("LogLevel").GetString() == "Critical");
        Assert.Equal(typeof(ExceptionHandlingMiddleware).FullName, critical.GetProperty("Category").GetString());
        AssertUtc(critical.GetProperty("Timestamp").GetString()!);
        var state = critical.GetProperty("State");
        Assert.Equal("UnhandledRequestFailure", state.GetProperty("EventName").GetString());
        Assert.Equal(typeof(NativeLoggingWireTests).Assembly.GetName().Name, state.GetProperty("Service").GetString());
        Assert.Equal("POST", state.GetProperty("Method").GetString());
        Assert.Equal("/", state.GetProperty("Path").GetString());
        Assert.Equal((int)status, state.GetProperty("StatusCode").GetInt32());
        Assert.Equal(validation ? "ArgumentException" : "Exception", state.GetProperty("ExceptionType").GetString());
        Assert.Equal(responseJson.RootElement.GetProperty("traceId").GetString(), state.GetProperty("IncidentId").GetString());
        AssertUtc(state.GetProperty("OccurredAtUtc").GetString()!);
        Assert.Contains(critical.GetProperty("Scopes").EnumerateArray(), scope => scope.TryGetProperty("CorrelationId", out var value) && value.GetString() == correlation);
        var captured = Assert.Single(capture.Entries, value => value.Level == LogLevel.Critical);
        Assert.Null(captured.Exception);
        Assert.DoesNotContain("private-", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-", body, StringComparison.Ordinal);
        Assert.All(capture.Entries, entry => Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal));
    }

    private static JsonElement[] ReadEntries(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private static void AssertUtc(string timestamp) => Assert.Equal(TimeSpan.Zero,
        DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new Entry(category, level, formatter(state, exception), exception));
    }

    private sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);
}
