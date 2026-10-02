using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class PrivateFailureProducerContractTests
{
    [Theory]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void Private_formatter_emits_native_severity_without_rendering_protected_text(LogLevel level, string severity)
    {
        var fields = new Dictionary<string, object?>
        {
            ["EventName"] = "DependencyRequestFailure",
            ["Dependency"] = "CountryService",
            ["Operation"] = "HttpRequest",
            ["StatusCode"] = 503,
            ["Body"] = "private-body-sentinel",
            ["Email"] = "private-email-sentinel@example.invalid",
            ["Url"] = "https://example.invalid/private-url-sentinel?token=private-token-sentinel",
            ["{OriginalFormat}"] = "private-template-sentinel"
        };
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["Cookie"] = "private-cookie-sentinel",
            ["Authorization"] = "private-authorization-sentinel",
            ["Customer"] = "private-customer-sentinel"
        });
        var entry = new LogEntry<Dictionary<string, object?>>(level, "Controlled.Dependency", new EventId(5101), fields,
            new InvalidOperationException("private-exception-sentinel", new Exception("private-inner-sentinel")),
            (_, _) => throw new InvalidOperationException("Rendered message callback must not run"));

        string output = Render(entry, scopes);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(severity, root.GetProperty("severity").GetString());
        Assert.Equal("DependencyRequestFailure", root.GetProperty("EventName").GetString());
        Assert.Equal("CountryService", root.GetProperty("Dependency").GetString());
        Assert.Equal("HttpRequest", root.GetProperty("Operation").GetString());
        Assert.Equal(503, root.GetProperty("StatusCode").GetInt32());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("State", out _));
        Assert.False(root.TryGetProperty("Scopes", out _));
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.None)]
    public void Private_formatter_keeps_success_and_disabled_levels_quiet(LogLevel level)
    {
        var entry = new LogEntry<string>(level, "Controlled.Success", default, "private-success-sentinel", null,
            (_, _) => throw new InvalidOperationException("Quiet entries must not be rendered"));

        Assert.Equal(string.Empty, Render(entry));
    }

    [Fact]
    public void Reserved_fields_cannot_be_spoofed_or_repeated_by_state_and_scopes()
    {
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("severity", "INFO"), new("logger", "private-logger-sentinel"),
            new("message", "private-message-sentinel"), new("exceptionType", "ForgedException"),
            new("EventName", "DependencyRequestFailure"), new("EventName", "ForgedEvent"),
            new("StatusCode", 503), new("StatusCode", 200)
        };
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["severity"] = "INFO",
            ["EventName"] = "ForgedScopeEvent",
            ["StatusCode"] = 200
        });
        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(LogLevel.Error, "Controlled.Dependency",
            new EventId(5101), fields, new InvalidOperationException(), (_, _) => "private-rendered-sentinel");

        string output = Render(entry, scopes);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("ERROR", root.GetProperty("severity").GetString());
        Assert.Equal("Controlled.Dependency", root.GetProperty("logger").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.Equal("DependencyRequestFailure", root.GetProperty("EventName").GetString());
        Assert.Equal(503, root.GetProperty("StatusCode").GetInt32());
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowlisted_fields_require_scalar_types_and_bounded_code_owned_values()
    {
        var fields = new Dictionary<string, object?>
        {
            ["StatusCode"] = "private-status-sentinel",
            ["ElapsedMs"] = true,
            ["AttemptCount"] = new object(),
            ["Operation"] = "private-operation-sentinel/path",
            ["Dependency"] = new string('a', 97),
            ["DiagnosticId"] = "not-a-guid",
            ["TraceId"] = "private-trace-sentinel",
            ["Synthetic"] = "true"
        };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled.Dependency", default,
            fields, null, (_, _) => "private-rendered-sentinel");

        string output = Render(entry);

        using var document = JsonDocument.Parse(output);
        foreach (string field in fields.Keys)
        {
            Assert.False(document.RootElement.TryGetProperty(field, out _));
        }
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private-category-sentinel@example.invalid")]
    [InlineData("private-category-sentinel/path")]
    public void Non_code_owned_category_is_replaced_without_echo(string category)
    {
        var entry = new LogEntry<string>(LogLevel.Error, category, default, "unused", null, (_, _) => "unused");

        string output = Render(entry);

        using var document = JsonDocument.Parse(output);
        Assert.Equal("ConfiguredLogger", document.RootElement.GetProperty("logger").GetString());
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
    }

    [Fact]
    public void First_valid_scalar_survives_and_safe_scope_only_fills_absent_fields()
    {
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("StatusCode", "not-an-integer"), new("StatusCode", 503), new("StatusCode", 200),
            new("Operation", "invalid/path"), new("Operation", "HttpRequest")
        };
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["Dependency"] = "CountryService",
            ["StatusCode"] = 200,
            ["ElapsedMs"] = 12L
        });
        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(LogLevel.Error, "Controlled.Dependency",
            default, fields, null, (_, _) => "unused");

        using var document = JsonDocument.Parse(Render(entry, scopes));

        Assert.Equal(503, document.RootElement.GetProperty("StatusCode").GetInt32());
        Assert.Equal("HttpRequest", document.RootElement.GetProperty("Operation").GetString());
        Assert.Equal("CountryService", document.RootElement.GetProperty("Dependency").GetString());
        Assert.Equal(12L, document.RootElement.GetProperty("ElapsedMs").GetInt64());
    }

    private static string Render<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes = null)
    {
        var type = typeof(MalievCloudJsonConsoleFormatter).Assembly.GetType(
            "Maliev.Aspire.ServiceDefaults.Logging.PrivateFailureConsoleFormatter");
        Assert.True(type is not null, "Opt-in private failure producer is not implemented.");
        var formatter = Assert.IsAssignableFrom<ConsoleFormatter>(Activator.CreateInstance(type!));
        using var writer = new StringWriter();
        formatter.Write(entry, scopes, writer);
        return writer.ToString();
    }

    [Fact]
    public void State_enumeration_stops_after_sixty_four_fields()
    {
        var entry = new LogEntry<IEnumerable<KeyValuePair<string, object?>>>(LogLevel.Error, "Controlled.Dependency",
            default, OversizedFields(), null, (_, _) => "unused");

        using var document = JsonDocument.Parse(Render(entry));

        Assert.Equal("ERROR", document.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public void Scope_enumeration_stops_after_sixteen_scopes()
    {
        var entry = new LogEntry<string>(LogLevel.Error, "Controlled.Dependency", default, "unused", null,
            (_, _) => "unused");

        using var document = JsonDocument.Parse(Render(entry, new BudgetedScopeProvider()));

        Assert.Equal("ERROR", document.RootElement.GetProperty("severity").GetString());
    }

    private static IEnumerable<KeyValuePair<string, object?>> OversizedFields()
    {
        for (int index = 0; index < 64; index++)
        {
            yield return new("NotAllowlisted", index);
        }
        throw new InvalidOperationException("State enumeration exceeded the independent 64-field budget");
    }

    private sealed class BudgetedScopeProvider : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
        {
            for (int index = 0; index < 16; index++)
            {
                callback(new Dictionary<string, object?> { ["NotAllowlisted"] = index }, state);
            }
            throw new InvalidOperationException("Scope enumeration exceeded the independent 16-scope budget");
        }

        public IDisposable Push(object? state) => throw new NotSupportedException();
    }
}
