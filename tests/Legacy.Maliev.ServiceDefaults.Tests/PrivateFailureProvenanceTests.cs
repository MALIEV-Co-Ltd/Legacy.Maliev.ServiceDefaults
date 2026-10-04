using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Migration regressions for ProductionDiagnostics.cs at
// a59193ae2ac030d0e1d373ce4ec50c1b35437391. These use the existing formatter API;
// missing provenance must fail on emitted JSON, not on nonexistent production members.
public sealed class PrivateFailureProvenanceTests
{
    [Theory]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void Write_FailureLevel_EmitsAuthoritativeUtcTimestamp(LogLevel level, string severity)
    {
        var entry = CreateEntry(level, SpoofedProvenance(), null);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        string output = Render(in entry);

        DateTimeOffset after = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(severity, root.GetProperty("severity").GetString());
        string timestamp = Assert.IsType<string>(root.GetProperty("occurredAtUtc").GetString());
        var actual = DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.InRange(actual, before, after);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Write_Failure_EmitsEntryAssemblyIdentityNotStateOrScopeIdentity()
    {
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(SpoofedProvenance());
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(), null);
        var assembly = Assembly.GetEntryAssembly();

        using var document = JsonDocument.Parse(Render(in entry, scopes));

        // Original service/version provenance is the application entry assembly,
        // not the ServiceDefaults library or the test assembly selected by hand.
        Assert.Equal(assembly?.GetName().Name, document.RootElement.GetProperty("service").GetString());
        Assert.Equal(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            document.RootElement.GetProperty("deploymentVersion").GetString());
        AssertUniquePropertyNames(document.RootElement);
    }

    [Fact]
    public void Write_ActiveW3cActivity_EmitsActualTraceAndSpanNotSpoofedFields()
    {
        using var activity = new Activity("ControlledFailure").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.Same(activity, Activity.Current);
        Assert.NotEqual(default(ActivityTraceId), activity.TraceId);
        Assert.NotEqual(default(ActivitySpanId), activity.SpanId);
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(SpoofedProvenance());
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(), null);

        using var document = JsonDocument.Parse(Render(in entry, scopes));

        var root = document.RootElement;
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        Assert.False(root.TryGetProperty("TraceId", out _));
        Assert.False(root.TryGetProperty("SpanId", out _));
        AssertUniquePropertyNames(root);
    }

    [Fact]
    public void Write_NoActivity_OmitsTraceAndSpanDespiteValidLookingStateAndScope()
    {
        var previousActivity = Activity.Current;
        try
        {
            Activity.Current = null;
            var scopes = new LoggerExternalScopeProvider();
            using var scope = scopes.Push(SpoofedProvenance());
            var entry = CreateEntry(LogLevel.Warning, SpoofedProvenance(), null);

            using var document = JsonDocument.Parse(Render(in entry, scopes));

            foreach (string field in new[] { "traceId", "spanId", "TraceId", "SpanId" })
            {
                Assert.False(document.RootElement.TryGetProperty(field, out _));
            }
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public void Write_ThrownNestedException_EmitsTypesAndCodeOwnedOuterMethodWithoutPrivateText()
    {
        var exception = CaptureNestedFailure();
        Assert.NotNull(exception.TargetSite);
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(SpoofedProvenance());
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(), exception);

        string output = Render(in entry, scopes);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.Equal(typeof(ArgumentException).FullName, root.GetProperty("innerExceptionType").GetString());
        // sourceLocation in the committed source is type.method, never a filename,
        // stack trace, line number, or caller-supplied state value.
        Assert.Equal(typeof(PrivateFailureProvenanceTests).FullName + "." + nameof(ThrowOuterFailure),
            root.GetProperty("sourceLocation").GetString());
        Assert.DoesNotContain("private-provenance-", output, StringComparison.Ordinal);
        AssertUniquePropertyNames(root);
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.None)]
    public void Write_SuppressedLevel_DoesNotEnumerateOrRenderProvenance(LogLevel level)
    {
        using var activity = new Activity("ControlledSuppressedFailure").SetIdFormat(ActivityIdFormat.W3C).Start();
        var entry = new LogEntry<IEnumerable<KeyValuePair<string, object?>>>(level, "Controlled.Failure",
            new EventId(5101), ThrowOnEnumeration(), CaptureNestedFailure(),
            (_, _) => throw new InvalidOperationException("Suppressed formatter callback must not run"));

        Assert.Equal(string.Empty, Render(in entry, new ThrowOnScopeTraversal()));
    }

    [Fact]
    public void Write_ProvenanceWithProtectedPayloads_DoesNotExposeMessagesDataOrArbitraryObjects()
    {
        var fields = SpoofedProvenance();
        fields.Add(new("Body", new ProtectedValue()));
        fields.Add(new("Authorization", "private-provenance-authorization"));
        fields.Add(new("Cookie", "private-provenance-cookie"));
        fields.Add(new("Email", "private-provenance-person@example.invalid"));
        fields.Add(new("Url", "https://example.invalid/private-provenance-url?token=secret"));
        fields.Add(new("{OriginalFormat}", "private-provenance-template"));
        fields.Add(new("Operation", "private-provenance-operation/path"));
        fields.Add(new("Dependency", new string('a', 97)));
        var exception = CaptureNestedFailure();
        exception.Data["Private"] = new ProtectedValue();
        var scopes = new LoggerExternalScopeProvider();
        using var scope = scopes.Push(fields);
        var entry = CreateEntry(LogLevel.Critical, fields, exception);

        string output = Render(in entry, scopes);

        using var document = JsonDocument.Parse(output);
        foreach (string name in new[] { "Body", "Authorization", "Cookie", "Email", "Url", "{OriginalFormat}",
            "Operation", "Dependency", "Data", "StackTrace", "State", "Scopes" })
        {
            Assert.False(document.RootElement.TryGetProperty(name, out _));
        }
        Assert.DoesNotContain("private-provenance-", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        AssertUniquePropertyNames(document.RootElement);
    }

    [Fact]
    public void Write_ProvenanceWithOversizedStateAndScopes_PreservesIndependentTraversalBudgets()
    {
        var entry = new LogEntry<IEnumerable<KeyValuePair<string, object?>>>(LogLevel.Error, "Controlled.Failure",
            new EventId(5101), BudgetedFields(), CaptureNestedFailure(),
            (_, _) => throw new InvalidOperationException("Formatter callback must not run"));

        using var document = JsonDocument.Parse(Render(in entry, new BudgetedScopes()));

        Assert.Equal("ERROR", document.RootElement.GetProperty("severity").GetString());
        Assert.False(document.RootElement.TryGetProperty("NotAllowlisted", out _));
        AssertUniquePropertyNames(document.RootElement);
    }

    private static List<KeyValuePair<string, object?>> SpoofedProvenance() =>
    [
        new("occurredAtUtc", "2000-01-01T00:00:00.0000000+00:00"),
        new("service", "private-provenance-forged-service"),
        new("deploymentVersion", "private-provenance-forged-version"),
        new("exceptionType", "ForgedOuterException"),
        new("innerExceptionType", "ForgedInnerException"),
        new("sourceLocation", "private-provenance-forged/path.cs:42"),
        new("traceId", new string('1', 32)), new("spanId", new string('2', 16)),
        new("TraceId", new string('3', 32)), new("SpanId", new string('4', 16))
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_MissingThrowSite_DoesNotInventSourceLocation(bool includeException)
    {
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(),
            includeException ? new InvalidOperationException("private-provenance-unthrown") : null);
        using var document = JsonDocument.Parse(Render(in entry));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("sourceLocation").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("innerExceptionType").ValueKind);
    }

    [Fact]
    public void Write_HierarchicalActivity_DoesNotInventW3cIdentifiers()
    {
        using var activity = new Activity("ControlledHierarchicalFailure")
            .SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(), null);
        using var document = JsonDocument.Parse(Render(in entry));
        Assert.False(document.RootElement.TryGetProperty("traceId", out _));
        Assert.False(document.RootElement.TryGetProperty("spanId", out _));
    }

    [Fact]
    public async Task Write_AsyncThrowSite_FailsClosedForCompilerGeneratedType()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(ThrowAsyncFailure);
        Assert.NotNull(exception.TargetSite);
        var entry = CreateEntry(LogLevel.Error, SpoofedProvenance(), exception);
        var output = Render(in entry);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("sourceLocation").ValueKind);
        Assert.Equal(typeof(InvalidOperationException).FullName,
            document.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-provenance-", output, StringComparison.Ordinal);
    }

    private static async Task ThrowAsyncFailure()
    {
        await Task.Yield();
        throw new InvalidOperationException("private-provenance-async-message");
    }

    private static LogEntry<List<KeyValuePair<string, object?>>> CreateEntry(LogLevel level,
        List<KeyValuePair<string, object?>> fields, Exception? exception) =>
        new(level, "Controlled.Failure", new EventId(5101), fields, exception,
            (_, _) => throw new InvalidOperationException("Formatter callback must not run"));

    private static string Render<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new PrivateFailureConsoleFormatter().Write(in entry, scopes, writer);
        return writer.ToString();
    }

    private static void AssertUniquePropertyNames(JsonElement root)
    {
        string[] names = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static InvalidOperationException CaptureNestedFailure()
    {
        try
        {
            ThrowOuterFailure();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Controlled throw site unexpectedly returned");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOuterFailure()
    {
        try
        {
            ThrowInnerFailure();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("private-provenance-outer-message", exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInnerFailure() => throw new ArgumentException("private-provenance-inner-message");

    private static IEnumerable<KeyValuePair<string, object?>> ThrowOnEnumeration()
    {
        yield return FailStateEnumeration();
    }

    private static KeyValuePair<string, object?> FailStateEnumeration() =>
        throw new InvalidOperationException("Suppressed state must not be enumerated");

    private static IEnumerable<KeyValuePair<string, object?>> BudgetedFields()
    {
        for (int index = 0; index < 64; index++)
        {
            yield return new("NotAllowlisted", new ProtectedValue());
        }
        throw new InvalidOperationException("State traversal exceeded the existing 64-field budget");
    }

    private sealed class ProtectedValue
    {
        public override string ToString() => throw new InvalidOperationException("Protected value must not be rendered");
    }

    private sealed class ThrowOnScopeTraversal : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            throw new InvalidOperationException("Suppressed scopes must not be traversed");

        public IDisposable Push(object? state) => throw new NotSupportedException();
    }

    private sealed class BudgetedScopes : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
        {
            for (int index = 0; index < 16; index++)
            {
                callback(BudgetedFields(), state);
            }
            throw new InvalidOperationException("Scope traversal exceeded the existing 16-scope budget");
        }

        public IDisposable Push(object? state) => throw new NotSupportedException();
    }
}
