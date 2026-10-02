using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests;

[Collection("Native console output")]
public sealed class PrivateStartupBoundaryContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_startup_runs_once_without_output_or_exit_change(bool asynchronous)
    {
        int previous = Environment.ExitCode;
        using var output = new StringWriter();
        var logger = new CaptureLogger();
        int calls = 0;
        try
        {
            Environment.ExitCode = 37;
            await Run(asynchronous, () => { calls++; return Task.CompletedTask; }, logger, output);
            Assert.Equal(1, calls);
            Assert.Equal(37, Environment.ExitCode);
            Assert.Empty(logger.Events);
            Assert.Equal(string.Empty, output.ToString());
        }
        finally { Environment.ExitCode = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supplied_logger_receives_only_fixed_failure_metadata(bool asynchronous)
    {
        int previous = Environment.ExitCode;
        var logger = new CaptureLogger();
        using var output = new StringWriter();
        int calls = 0;
        try
        {
            Environment.ExitCode = 37;
            var originalUnhandled = await Record.ExceptionAsync(() => Run(asynchronous,
                () => { calls++; throw new InvalidOperationException("private-startup-sentinel"); }, logger, output));
            Assert.Null(originalUnhandled);
            Assert.Equal(1, calls);
            Assert.Equal(1, Environment.ExitCode);
            var entry = Assert.Single(logger.Events);
            Assert.Equal(LogLevel.Critical, entry.Level);
            Assert.Equal(5102, entry.Id.Id);
            Assert.Equal("StartupFailure", entry.Id.Name);
            Assert.Null(entry.Exception);
            Assert.Equal("StartupFailure", entry.Fields["EventName"]);
            Assert.Equal("HostInitialization", entry.Fields["Operation"]);
            Assert.Equal(new[] { "EventName", "Operation" }, entry.Fields.Keys.Order().ToArray());
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
            Assert.Equal(string.Empty, output.ToString());
        }
        finally { Environment.ExitCode = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_output_renders_one_safe_native_critical_without_host_logger(bool asynchronous)
    {
        int previous = Environment.ExitCode;
        using var output = new StringWriter();
        try
        {
            Environment.ExitCode = 37;
            var originalUnhandled = await Record.ExceptionAsync(() => Run(asynchronous,
                () => Task.FromException(new InvalidOperationException("private-outer-sentinel", new Exception("private-inner-sentinel"))), null, output));
            Assert.Null(originalUnhandled);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("CRITICAL", json.RootElement.GetProperty("severity").GetString());
            Assert.Equal(5102, json.RootElement.GetProperty("eventId").GetInt32());
            Assert.Equal("StartupFailure", json.RootElement.GetProperty("EventName").GetString());
            Assert.Equal("HostInitialization", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal(typeof(InvalidOperationException).FullName, json.RootElement.GetProperty("exceptionType").GetString());
            Assert.DoesNotContain("private-", output.ToString(), StringComparison.Ordinal);
            Assert.False(json.RootElement.TryGetProperty("State", out _));
            Assert.False(json.RootElement.TryGetProperty("Scopes", out _));
        }
        finally { Environment.ExitCode = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failing_diagnostic_sink_cannot_replace_startup_failure_or_exit_one(bool loggerFailure)
    {
        int previous = Environment.ExitCode;
        var originalError = Console.Error;
        using var fallback = new StringWriter();
        try
        {
            Environment.ExitCode = 37;
            Console.SetError(fallback);
            var originalUnhandled = await Record.ExceptionAsync(() => Run(false,
                () => throw new InvalidOperationException("private-original-sentinel"),
                loggerFailure ? new CaptureLogger(true) : null, loggerFailure ? null : new ThrowingWriter()));
            Assert.Null(originalUnhandled);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(string.Empty, fallback.ToString());
        }
        finally { Console.SetError(originalError); Environment.ExitCode = previous; }
    }

    [Fact]
    public void Explicit_reporting_uses_private_output_and_sets_failing_exit()
    {
        int previous = Environment.ExitCode;
        using var output = new StringWriter();
        try
        {
            Environment.ExitCode = 37;
            PrivateStartupBoundary.ReportFailure(new InvalidOperationException("private-explicit-sentinel"), null, output);
            Assert.Equal(1, Environment.ExitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("StartupFailure", json.RootElement.GetProperty("EventName").GetString());
            Assert.DoesNotContain("private-", output.ToString(), StringComparison.Ordinal);
        }
        finally { Environment.ExitCode = previous; }
    }

    private static async Task Run(bool asynchronous, Func<Task> operation, ILogger? logger, TextWriter? output)
    {
        if (asynchronous)
            await PrivateStartupBoundary.RunAsync(operation, logger, output);
        else
            PrivateStartupBoundary.Run(() => operation().GetAwaiter().GetResult(), logger, output);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("private-writer-sentinel");
    }

    private sealed record Event(LogLevel Level, EventId Id, Exception? Exception, string Message, Dictionary<string, object?> Fields);

    private sealed class CaptureLogger(bool fail = false) : ILogger
    {
        public List<Event> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (fail) throw new IOException("private-logger-sentinel");
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value);
            Events.Add(new(level, id, exception, formatter(state, exception), fields));
        }
    }
}
