using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

/// <summary>Exercises opt-in provenance at the unchanged generic terminal HTTP boundary.</summary>
public sealed class PrivateTerminalProvenanceTests
{
    private const string Canary = "private-terminal-provenance-canary";

    /// <summary>Preserves the generic response predicate and response identity with either provenance setting.</summary>
    /// <param name="enabled">Whether bounded runtime provenance is selected.</param>
    /// <param name="status">The controlled terminal response status.</param>
    /// <returns>A task completing after the unchanged response and event contract is checked.</returns>
    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 302)]
    [InlineData(true, 302)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    [InlineData(false, 404)]
    [InlineData(true, 404)]
    [InlineData(false, 429)]
    [InlineData(true, 429)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task GenericResponsesKeepPredicateAndIdentity(bool enabled, int status)
    {
        using var fixture = new Fixture(enabled);
        fixture.Transport.Response = new((HttpStatusCode)status) { Content = new StringContent(Canary) };
        using var request = Request();

        using var response = await fixture.Client.SendWithPrivateFailureObservationAsync(request);

        Assert.Same(fixture.Transport.Response, response);
        Assert.Equal(Canary, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Transport.Calls);
        if (status < 500) Assert.Empty(fixture.Events.Records);
        else
        {
            var record = Assert.Single(fixture.Events.Records);
            Assert.Equal(503, record.Fields["StatusCode"]);
            Assert.Null(record.Exception);
            using var json = JsonDocument.Parse(record.Json);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceLocation").ValueKind);
        }
    }

    /// <summary>Captures actual async or closure throw-site metadata without retaining the exception in logging state.</summary>
    /// <param name="enabled">Whether bounded runtime provenance is selected.</param>
    /// <param name="closure">Whether the real throw occurs in a compiler-generated closure.</param>
    /// <returns>A task completing after transport identity, scalar state and emitted JSON are checked.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualThrowSitesPreserveIdentityAndClosedSink(bool enabled, bool closure)
    {
        using var fixture = new Fixture(enabled);
        var expected = await CaptureFailureAsync(closure);
        fixture.Transport.Failure = expected;
        using var request = Request();

        var actual = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Client.SendWithPrivateFailureObservationAsync(request));

        Assert.Same(expected, actual);
        var record = Assert.Single(fixture.Events.Records);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(new EventId(5101, "DependencyRequestFailure"), record.Id);
        Assert.Equal("DependencyRequestFailure Dependency=ControlledDependency Operation=HttpRequest", record.Message);
        Assert.Equal(new[] { "EventName", "Dependency", "Operation", "{OriginalFormat}" }, record.Fields.Keys);
        Assert.Null(record.Exception);
        Assert.DoesNotContain(Canary, record.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, record.Message, StringComparison.Ordinal);
        Assert.All(record.Fields.Values, value => Assert.True(value is null or string or int or long));
        using var json = JsonDocument.Parse(record.Json);
        if (enabled)
        {
            AssertRuntime(json.RootElement, expected);
            var state = Assert.IsType<PrivateFailureProvenanceState>(record.State);
            AssertScalarSnapshot(state.Provenance);
        }
        else
        {
            Assert.IsNotType<PrivateFailureProvenanceState>(record.State);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceLocation").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
        }
    }

    /// <summary>Preserves the existing status-bearing transport exception event even for a 4xx status.</summary>
    /// <returns>A task completing after identity and safe status metadata are checked.</returns>
    [Fact]
    public async Task StatusBearingTransportFailureKeepsExistingCatchPolicy()
    {
        using var fixture = new Fixture(true);
        var expected = new HttpRequestException(Canary, null, HttpStatusCode.NotFound);
        fixture.Transport.Failure = expected;
        using var request = Request();

        var actual = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Client.SendWithPrivateFailureObservationAsync(request));

        Assert.Same(expected, actual);
        var record = Assert.Single(fixture.Events.Records);
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(404, record.Fields["StatusCode"]);
        using var json = JsonDocument.Parse(record.Json);
        AssertRuntime(json.RootElement, actual);
    }

    /// <summary>Does not broaden observation to arbitrary application exceptions.</summary>
    /// <returns>A task completing after the original exception and quiet observer are checked.</returns>
    [Fact]
    public async Task UnexpectedExceptionDoesNotGainGenericObservation()
    {
        using var fixture = new Fixture(true);
        var expected = new InvalidOperationException(Canary);
        fixture.Transport.Failure = expected;
        using var request = Request();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Client.SendWithPrivateFailureObservationAsync(request));

        Assert.Same(expected, actual);
        Assert.Empty(fixture.Events.Records);
    }

    /// <summary>Leaves caller-owned failure observation quiet without replacing transport failure.</summary>
    /// <returns>A task completing after caller ownership and exception identity are checked.</returns>
    [Fact]
    public async Task CallerOwnedFailureRemainsQuiet()
    {
        using var fixture = new Fixture(true);
        var expected = await CaptureFailureAsync(false);
        fixture.Transport.Failure = expected;
        using var request = Request();
        request.Options.Set(new HttpRequestOptionsKey<bool>("Maliev.DependencyFailureOwnedByCaller"), true);
        var observation = new PrivateDependencyFailureObservation();

        var actual = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Client.SendWithPrivateFailureObservationAsync(request, default, observation));

        Assert.Same(expected, actual);
        Assert.False(observation.WasObserved);
        Assert.Empty(fixture.Events.Records);
    }

    /// <summary>Retains actual caller cancellation without recording a terminal failure.</summary>
    /// <returns>A task completing after the controlled transport exits and request state is cleared.</returns>
    [Fact]
    public async Task CallerCancellationStaysQuietAndReleasesRendezvous()
    {
        using var fixture = new Fixture(true);
        using var caller = new CancellationTokenSource();
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        var pending = fixture.Client.SendWithPrivateFailureObservationAsync(request, caller.Token, observation);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        caller.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Empty(fixture.Events.Records);
        Assert.False(observation.WasObserved);
        Assert.All(request.Options, pair => Assert.Null(pair.Value));
    }

    /// <summary>Captures the real native deadline wrapper once and keeps ownership established before sink failure.</summary>
    /// <param name="throwingSink">Whether the controlled logging provider throws after observing the event.</param>
    /// <returns>A task completing after deadline rewriting and bounded metadata are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDeadlineKeepsOnceOwnershipAndRealWrapper(bool throwingSink)
    {
        using var fixture = new Fixture(true, TimeSpan.FromSeconds(1));
        fixture.Events.ThrowOnRecord = throwingSink;
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        bool ownedAtSink = false;
        fixture.Events.OnRecord = () => ownedAtSink = observation.WasObserved;
        var pending = fixture.Client.SendWithPrivateFailureObservationAsync(request, default, observation);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var actual = await Assert.ThrowsAsync<TaskCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(fixture.Transport.Cancellation, Assert.IsType<TimeoutException>(actual.InnerException).InnerException);
        Assert.True(ownedAtSink);
        Assert.True(observation.WasObserved);
        Assert.Equal(1, fixture.Transport.Calls);
        var record = Assert.Single(fixture.Events.Records);
        Assert.Null(record.Exception);
        using var json = JsonDocument.Parse(record.Json);
        AssertRuntime(json.RootElement, actual);
        Assert.All(request.Options, pair => Assert.Null(pair.Value));
    }

    /// <summary>Rejects conflicting opt-in modes while repeated identical selection remains idempotent.</summary>
    [Fact]
    public void SelectionIsExplicitAndCannotSilentlyChangeExistingDefaults()
    {
        var services = new ServiceCollection();
        var client = services.AddHttpClient("Guarded");
        Assert.Same(client, client.AddPrivateFailureObservation("ControlledDependency", true));
        int count = services.Count;
        Assert.Same(client, client.AddPrivateFailureObservation("ControlledDependency", true));
        Assert.Equal(count, services.Count);
        Assert.Throws<InvalidOperationException>(() => client.AddPrivateFailureObservation("ControlledDependency"));
        Assert.Equal(count, services.Count);
        var other = services.AddHttpClient("Default");
        other.AddPrivateFailureObservation("ControlledDependency");
        count = services.Count;
        Assert.Throws<InvalidOperationException>(() => other.AddPrivateFailureObservation("ControlledDependency", true));
        Assert.Equal(count, services.Count);
    }

    /// <summary>Does not accept dictionary or scope lookalikes as runtime provenance.</summary>
    /// <returns>A task completing after genuine and forged emitted provenance are compared.</returns>
    [Fact]
    public async Task TrustedSnapshotCannotBeReplacedByStateOrScopes()
    {
        var failure = await CaptureFailureAsync(false);
        var state = PrivateFailureProvenanceState.ForDependency("ControlledDependency", "HttpRequest", null,
            PrivateFailureProvenanceState.Capture(failure));
        var forged = new Dictionary<string, object?>
        {
            ["exceptionType"] = "Forged.Type",
            ["innerExceptionType"] = "Forged.Inner",
            ["sourceLocation"] = "Forged.Location",
            ["ExceptionType"] = "Forged.Type",
            ["service"] = "Forged.Service",
            ["deploymentVersion"] = "Forged.Version",
            ["traceId"] = new string('1', 32),
            ["spanId"] = new string('2', 16)
        };
        var scopes = new LoggerExternalScopeProvider();
        using var forgedScope = scopes.Push(forged);
        using var trustedScope = scopes.Push(state);
        using var writer = new StringWriter();
        var entry = new LogEntry<PrivateFailureProvenanceState>(LogLevel.Error, "Controlled", new EventId(5101),
            state, null, (_, _) => throw new InvalidOperationException("Do not render arbitrary text"));
        new PrivateFailureConsoleFormatter().Write(entry, scopes, writer);
        using (var json = JsonDocument.Parse(writer.ToString())) AssertRuntime(json.RootElement, failure);
        writer.GetStringBuilder().Clear();
        var lookalike = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled", new EventId(5101),
            forged, null, (_, _) => Canary);
        new PrivateFailureConsoleFormatter().Write(lookalike, scopes, writer);
        using var rejected = JsonDocument.Parse(writer.ToString());
        Assert.Equal(JsonValueKind.Null, rejected.RootElement.GetProperty("sourceLocation").ValueKind);
        Assert.Equal(JsonValueKind.Null, rejected.RootElement.GetProperty("exceptionType").ValueKind);
        Assert.Equal(JsonValueKind.Null, rejected.RootElement.GetProperty("innerExceptionType").ValueKind);
        Assert.DoesNotContain("Forged", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Does not read exception messages, data, stack text or arbitrary string conversion during capture.</summary>
    [Fact]
    public void CaptureDoesNotReadProtectedExceptionPayloads()
    {
        Action fail = () => throw new PoisonedTransportException();
        var actual = Assert.Throws<PoisonedTransportException>(fail);
        var state = PrivateFailureProvenanceState.ForDependency("ControlledDependency", "HttpRequest", null,
            PrivateFailureProvenanceState.Capture(actual));
        using var writer = new StringWriter();
        var entry = new LogEntry<PrivateFailureProvenanceState>(LogLevel.Error, "Controlled", new EventId(5101),
            state, null, (_, _) => throw new InvalidOperationException("Do not invoke arbitrary formatting"));

        new PrivateFailureConsoleFormatter().Write(entry, null, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        AssertRuntime(json.RootElement, actual);
        Assert.DoesNotContain(Canary, writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Retains the existing formatter's bounds and missing-throw-site behavior for captured metadata.</summary>
    [Fact]
    public void SnapshotCaptureUsesExistingBoundsAndDoesNotInventThrowSites()
    {
        var snapshot = PrivateFailureProvenanceState.Capture(new HttpRequestException(Canary));
        Assert.Equal(typeof(HttpRequestException).FullName, snapshot.ExceptionType);
        Assert.Null(snapshot.InnerExceptionType);
        Assert.Null(snapshot.SourceLocation);
        Assert.Null(PrivateFailureConsoleFormatter.SafeTypeName(new string('A', 257)));
        Assert.Null(PrivateFailureConsoleFormatter.SafeTypeName("Unsafe/Type"));
        Assert.Null(PrivateFailureConsoleFormatter.SafeSourceLocation(null));
        AssertScalarSnapshot(snapshot);
    }

    /// <summary>Keeps a real exception argument authoritative over a different producer-owned snapshot.</summary>
    /// <returns>A task completing after the formatter's existing exception precedence is checked.</returns>
    [Fact]
    public async Task RealExceptionArgumentKeepsExistingFormatterPrecedence()
    {
        var first = await CaptureFailureAsync(false);
        var second = await CaptureFailureAsync(true);
        var state = PrivateFailureProvenanceState.ForDependency("ControlledDependency", "HttpRequest", null,
            PrivateFailureProvenanceState.Capture(first));
        using var writer = new StringWriter();
        var entry = new LogEntry<PrivateFailureProvenanceState>(LogLevel.Error, "Controlled", new EventId(5101),
            state, second, (_, _) => Canary);

        new PrivateFailureConsoleFormatter().Write(entry, null, writer);

        using var json = JsonDocument.Parse(writer.ToString());
        AssertRuntime(json.RootElement, second);
        Assert.DoesNotContain(Canary, writer.ToString(), StringComparison.Ordinal);
    }

    private static void AssertScalarSnapshot(PrivateFailureProvenanceState.Snapshot snapshot)
    {
        var fields = snapshot.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(3, fields.Length);
        Assert.All(fields, field => Assert.True(field.GetValue(snapshot) is null or string));
        Assert.All(fields, field => Assert.Equal(typeof(string), field.FieldType));
    }

    private static void AssertRuntime(JsonElement json, Exception failure)
    {
        var target = Assert.IsAssignableFrom<MethodBase>(failure.TargetSite);
        Assert.Equal(failure.GetType().FullName, json.GetProperty("exceptionType").GetString());
        Assert.Equal(failure.InnerException?.GetType().FullName, json.GetProperty("innerExceptionType").GetString());
        Assert.Equal(target.DeclaringType?.FullName + "." + target.Name,
            json.GetProperty("sourceLocation").GetString());
    }

    private static async Task<HttpRequestException> CaptureFailureAsync(bool closure)
    {
        try
        {
            if (closure)
            {
                Action fail = () => throw new HttpRequestException(Canary, new ArgumentException(Canary));
                fail();
            }
            else await ThrowAsync();
        }
        catch (HttpRequestException failure)
        {
            failure.Data["Protected"] = new ProtectedValue();
            return failure;
        }
        throw new InvalidOperationException("The controlled throw must run.");
    }

    private static async Task ThrowAsync()
    {
        await Task.Yield();
        throw new HttpRequestException(Canary, new ArgumentException(Canary));
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Post,
        "https://dependency.example.invalid/" + Canary + "?token=" + Canary);

    private sealed class PoisonedTransportException : HttpRequestException
    {
        public override string Message => throw new InvalidOperationException("Do not read Message");
        public override string? StackTrace => throw new InvalidOperationException("Do not read StackTrace");
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("Do not read Data");
        public override string ToString() => throw new InvalidOperationException("Do not render Exception");
    }

    private sealed class ProtectedValue
    {
        public override string ToString() => throw new InvalidOperationException("Do not render protected data");
    }

    private sealed class Fixture : IDisposable
    {
        public IHost Host { get; }
        public HttpClient Client { get; }
        public Transport Transport { get; } = new();
        public ProvenanceRecorder Events { get; } = new();

        public Fixture(bool enabled, TimeSpan? timeout = null)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Events);
            builder.Services.AddHttpClient("Observed", client => client.Timeout = timeout ?? Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => Transport)
                .AddPrivateFailureObservation("ControlledDependency", enabled);
            Host = builder.Build();
            Client = Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Observed");
        }

        public void Dispose()
        {
            try { Client.Dispose(); }
            finally
            {
                try { Host.Dispose(); }
                finally { Transport.Dispose(); }
            }
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public HttpResponseMessage? Response { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OperationCanceledException? Cancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            if (Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            if (Response is { } response) return response;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException failure)
            {
                Cancellation = failure;
                throw;
            }
            throw new InvalidOperationException("Controlled transport must complete through cancellation.");
        }
    }

    internal sealed record Record(LogLevel Level, EventId Id, object State, Exception? Exception,
        string Message, Dictionary<string, object?> Fields, string Json);

    internal sealed class ProvenanceRecorder : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<Record> Records { get; } = new();
        public bool ThrowOnRecord { get; set; }
        public Action? OnRecord { get; set; }
        public Action? OnWarning { get; set; }
        public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, this);
        public void Dispose() { }

        private sealed class Recorder(string category, ProvenanceRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category == "Maliev.Aspire.ServiceDefaults.Middleware.ExceptionHandlingMiddleware" && level == LogLevel.Warning)
                    owner.OnWarning?.Invoke();
                if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (values.GetValueOrDefault("EventName") as string is not ("DependencyRequestFailure" or "UnhandledRequestFailure" or "HealthProbeFailure")) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, id, state, exception, formatter);
                new PrivateFailureConsoleFormatter().Write(entry, owner.scopes, writer);
                owner.Records.Enqueue(new(level, id, state!, exception, formatter(state, exception), values, writer.ToString()));
                owner.OnRecord?.Invoke();
                if (owner.ThrowOnRecord) throw new InvalidOperationException(Canary);
            }
        }
    }
}
