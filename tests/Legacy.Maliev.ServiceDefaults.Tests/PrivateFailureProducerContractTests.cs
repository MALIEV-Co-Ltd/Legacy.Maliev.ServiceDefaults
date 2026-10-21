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


    [Theory]
    [InlineData("upload_invalid")]
    [InlineData("quote_invalid")]
    [InlineData("quote_expired")]
    [InlineData("upload_expired")]
    [InlineData("quote_mismatch")]
    public void Source_business_code_is_preserved_only_from_the_five_value_taxonomy(string code)
    {
        var fields = new Dictionary<string, object?> { ["Code"] = code, ["Payload"] = new ProtectedValue() };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Warning, "Controlled.Web", default,
            fields, null, (_, _) => throw new InvalidOperationException("Must not render"));
        using var document = JsonDocument.Parse(Render(entry));
        Assert.Equal(code, document.RootElement.GetProperty("Code").GetString());
        Assert.False(document.RootElement.TryGetProperty("Payload", out _));
    }

    [Theory]
    [InlineData("Code", "quote_Invalid")]
    [InlineData("Code", "quote_invalid ")]
    [InlineData("Code", "customer-file.pdf")]
    [InlineData("Code", "")]
    [InlineData("IncidentId", "01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("IncidentId", "{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("IncidentId", "00000000000000000000000000000000")]
    [InlineData("IncidentId", "private-customer-incident")]
    [InlineData("IncidentId", " 0123456789abcdef0123456789abcdef")]
    [InlineData("IncidentId", "0123456789abcdef0123456789abcdef ")]
    [InlineData("OccurredAtUtc", "2026-10-21")]
    [InlineData("OccurredAtUtc", "10/21/2026 10:00:00")]
    [InlineData("OccurredAtUtc", "2026-10-21T10:00:00.0000000")]
    [InlineData("OccurredAtUtc", "2026-10-21T10:00:00.0000000+7:00")]
    [InlineData("OccurredAtUtc", "private-customer-timestamp")]
    public void Source_incident_fields_reject_noncontract_strings_without_echo(string key, string value)
    {
        var fields = new Dictionary<string, object?> { [key] = value };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled.Web", default,
            fields, null, (_, _) => throw new InvalidOperationException("Must not render"));
        using var document = JsonDocument.Parse(Render(entry));
        Assert.False(document.RootElement.TryGetProperty(key, out _));
    }

    [Theory]
    [InlineData("2026-10-21T10:30:00.0000000+07:00", "2026-10-21T03:30:00.0000000+00:00")]
    [InlineData("2026-10-21T03:30:00.1234567+00:00", "2026-10-21T03:30:00.1234567+00:00")]
    public void Source_incident_timestamp_is_invariant_exact_roundtrip_and_normalized_utc(string timestamp, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var fields = new Dictionary<string, object?> { ["OccurredAtUtc"] = timestamp };
            var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled.Web", default, fields,
                null, (_, _) => throw new InvalidOperationException("Must not render"));
            using var document = JsonDocument.Parse(Render(entry));
            Assert.Equal(expected, document.RootElement.GetProperty("OccurredAtUtc").GetString());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Source_incident_fields_keep_first_valid_scalar_without_overriding_authoritative_provenance()
    {
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("IncidentId", "invalid"), new("IncidentId", "0123456789ABCDEF0123456789ABCDEF"),
            new("IncidentId", "abcdef0123456789abcdef0123456789"),
            new("Code", "quote_invalid"), new("Code", "upload_invalid"),
            new("OccurredAtUtc", "2026-10-21T10:30:00.0000000+07:00"),
            new("occurredAtUtc", "private-forged-time"), new("service", "private-forged-service"),
            new("deploymentVersion", "private-forged-version"), new("exceptionType", "private-forged-type"),
            new("traceId", "private-forged-trace"), new("spanId", "private-forged-span")
        };
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["IncidentId"] = "abcdef0123456789abcdef0123456789",
            ["Code"] = "quote_mismatch",
            ["OccurredAtUtc"] = "2026-10-20T03:30:00.0000000+00:00"
        });
        using var activity = new System.Diagnostics.Activity("ControlledIncident")
            .SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(LogLevel.Critical, "Controlled.Web", default,
            fields, new InvalidOperationException("private-exception"), (_, _) => throw new InvalidOperationException("Must not render"));
        var output = Render(entry, scopes);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("IncidentId").GetString());
        Assert.Equal("quote_invalid", root.GetProperty("Code").GetString());
        Assert.Equal("2026-10-21T03:30:00.0000000+00:00", root.GetProperty("OccurredAtUtc").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_incident_fields_ignore_nonstring_objects_and_allow_scope_to_fill_absent_valid_fields()
    {
        var fields = new Dictionary<string, object?>
        {
            ["Code"] = new ProtectedValue(),
            ["IncidentId"] = new ProtectedValue(),
            ["OccurredAtUtc"] = new ProtectedValue()
        };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled.Web", default, fields,
            null, (_, _) => throw new InvalidOperationException("Must not render"));
        using var rejected = JsonDocument.Parse(Render(entry));
        Assert.All(fields.Keys, key => Assert.False(rejected.RootElement.TryGetProperty(key, out _)));
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(new Dictionary<string, object?>
        {
            ["Code"] = "quote_expired",
            ["IncidentId"] = "0123456789abcdef0123456789abcdef",
            ["OccurredAtUtc"] = "2026-10-21T03:30:00.0000000+00:00"
        });
        using var accepted = JsonDocument.Parse(Render(entry, scopes));
        Assert.Equal("quote_expired", accepted.RootElement.GetProperty("Code").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", accepted.RootElement.GetProperty("IncidentId").GetString());
        Assert.Equal("2026-10-21T03:30:00.0000000+00:00", accepted.RootElement.GetProperty("OccurredAtUtc").GetString());
    }

    private sealed class ProtectedValue
    {
        public override string ToString() => throw new InvalidOperationException("Protected values must never be rendered.");
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
