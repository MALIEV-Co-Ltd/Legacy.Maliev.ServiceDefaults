using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Original source fields: 3d6506285a58671651d046e97a35fbb8885cea4f and
// 9e51e6c5da29de8e617b65b59d46882cde6d3b64. Unknown custom metadata is an explicit adaptation.
public sealed class PrivateDependencySourceMetadataTests
{
    [Theory]
    [InlineData(false, "GET")]
    [InlineData(true, "GET")]
    [InlineData(false, "HEAD")]
    [InlineData(true, "HEAD")]
    [InlineData(false, "POST")]
    [InlineData(true, "POST")]
    [InlineData(false, "PUT")]
    [InlineData(true, "PUT")]
    [InlineData(false, "DELETE")]
    [InlineData(true, "DELETE")]
    [InlineData(false, "CONNECT")]
    [InlineData(true, "CONNECT")]
    [InlineData(false, "OPTIONS")]
    [InlineData(true, "OPTIONS")]
    [InlineData(false, "TRACE")]
    [InlineData(true, "TRACE")]
    [InlineData(false, "PATCH")]
    [InlineData(true, "PATCH")]
    [InlineData(false, "private-custom-method-sentinel")]
    [InlineData(true, "private-custom-method-sentinel")]
    public async Task Normal_host_formatter_preserves_standalone_method_on_unknown_resource(bool typed, string method)
    {
        using var fixture = new Fixture(typed);
        using var request = Request(method);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        var entry = fixture.AssertFailure(method.StartsWith("private-", StringComparison.Ordinal) ? "UnknownMethod" : method, null);
        Assert.Equal("UnknownOperation", entry.Fields["Operation"]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    public async Task Normal_host_and_private_formatter_preserve_exact_known_type_or_explicit_unknown(bool typed, int kind)
    {
        Exception failure = kind switch
        {
            0 => new HttpRequestException("private-exception-sentinel", null, HttpStatusCode.BadGateway),
            1 => new TimeoutException("private-exception-sentinel"),
            2 => new OperationCanceledException("private-exception-sentinel"),
            3 => new TaskCanceledException("private-exception-sentinel"),
            4 => new Polly.Timeout.TimeoutRejectedException("private-exception-sentinel"),
            5 => new IOException("private-exception-sentinel"),
            6 => new InvalidOperationException("private-exception-sentinel"),
            7 => new ArgumentException("private-exception-sentinel"),
            8 => new NotSupportedException("private-exception-sentinel"),
            _ => new CustomerAccountSecretException()
        };
        failure.Data["private-data-sentinel"] = "private-data-sentinel";
        using var fixture = new Fixture(typed);
        fixture.Transport.Failure = failure;
        using var request = Request("POST");
        var actual = await Record.ExceptionAsync(() => fixture.Client.SendAsync(request));
        Assert.Same(failure, actual);
        fixture.AssertFailure("POST", kind == 9 ? "UnknownException" : failure.GetType().Name);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Existing_modes_do_not_gain_source_metadata(bool typed, int mode)
    {
        using var fixture = new Fixture(typed, mode);
        fixture.Transport.Status = HttpStatusCode.ServiceUnavailable;
        using var request = Request("POST");
        using var response = await fixture.Client.SendAsync(request);
        var entry = Assert.Single(fixture.Events.Entries);
        Assert.False(entry.Fields.ContainsKey("Method"));
        Assert.False(entry.Fields.ContainsKey("ExceptionType"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unselected_normal_host_client_remains_unobserved(bool typed)
    {
        using var fixture = new Fixture(typed, mode: 0);
        using var request = Request("POST");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_remains_quiet_with_normal_host_formatter(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Block = true;
        using var caller = new CancellationTokenSource();
        using var request = Request("GET");
        var pending = fixture.Client.SendWithPrivateFailureObservationAsync(request, caller.Token);
        try
        {
            await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(fixture.Events.Entries);
        }
        finally
        {
            caller.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_outer_native_deadline_records_actual_wrapper_type_once(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Block = true;
        fixture.Client.Timeout = TimeSpan.FromMilliseconds(100);
        using var request = Request("GET");
        var failure = await Record.ExceptionAsync(() => fixture.Client.SendWithPrivateFailureObservationAsync(request,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TaskCanceledException>(failure);
        fixture.AssertFailure("GET", "TaskCanceledException");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Source_metadata_stays_outside_retry_and_uses_only_terminal_outcome(bool typed, bool recovered)
    {
        using var fixture = new Fixture(typed, retry: true);
        fixture.Transport.Status = recovered ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
        using var request = Request("POST");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Same(fixture.Transport.Response, response);
        if (recovered) Assert.Empty(fixture.Events.Entries);
        else fixture.AssertFailure("POST", null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_provider_does_not_replace_exact_exception(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Events.Throw = true;
        fixture.Transport.Failure = new IOException("private-exception-sentinel");
        using var request = Request("GET");
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => fixture.Client.SendAsync(request)));
        fixture.AssertFailure("GET", "IOException");
    }

    [Theory]
    [InlineData("CustomerAccountSecretException")]
    [InlineData("private-type-sentinel/path")]
    [InlineData("System.IO.IOException")]
    public void Private_formatter_rejects_arbitrary_structured_type_and_keeps_runtime_provenance(string type)
    {
        var fields = new Dictionary<string, object?> { ["ExceptionType"] = type };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Controlled.Dependency", new EventId(5101),
            fields, new IOException("private-exception-sentinel"), (_, _) => "private-rendered-sentinel");
        using var writer = new StringWriter();
        new PrivateFailureConsoleFormatter().Write(entry, null, writer);
        using var json = JsonDocument.Parse(writer.ToString());
        Assert.False(json.RootElement.TryGetProperty("ExceptionType", out _));
        Assert.Equal(typeof(IOException).FullName, json.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerAccountSecretException", writer.ToString(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "private-route-sentinel?token=private-query-sentinel")
        { Content = new StringContent("private-body-sentinel") };
        request.Headers.Authorization = new("Bearer", "private-auth-sentinel");
        return request;
    }

    private sealed class CustomerAccountSecretException() : IOException("private-exception-sentinel") { }
    private sealed class SourceClient(HttpClient client) { public HttpClient Client { get; } = client; }
    private sealed record Entry(IReadOnlyDictionary<string, object?> Fields, string Wire, Exception? Exception);

    private sealed class Fixture : IDisposable
    {
        public Transport Transport { get; } = new();
        public Events Events { get; } = new();
        public IHost Host { get; }
        public HttpClient Client { get; }

        public Fixture(bool typed, int mode = 3, bool retry = false)
        {
            Transport.Retry = retry;
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.AddServiceDefaults();
            // Keep real normal-host console options/formatter DI; capture synchronous event writes without global Console mutation.
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Events);
            var registration = typed
                ? builder.Services.AddHttpClient<SourceClient>("SourceMetadata", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"))
                : builder.Services.AddHttpClient("SourceMetadata", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"));
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (retry) registration.AddHttpMessageHandler(() => new SingleRetryHandler());
            if (mode == 1) registration.AddPrivateFailureObservation("ControlledDependency");
            else if (mode == 2) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            else if (mode == 3) registration.AddPrivateFailureSourceObservation("ControlledDependency");
            Host = builder.Build();
            try
            {
                Assert.Equal(MalievCloudJsonConsoleFormatter.FormatterName,
                    Host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
                Events.Formatter = Assert.Single(Host.Services.GetServices<ConsoleFormatter>()
                    .OfType<MalievCloudJsonConsoleFormatter>());
                Client = typed ? Host.Services.GetRequiredService<SourceClient>().Client
                    : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SourceMetadata");
            }
            catch
            {
                try { Host.Dispose(); }
                finally { Transport.Dispose(); }
                throw;
            }
        }

        public Entry AssertFailure(string method, string? exceptionType)
        {
            var entry = Assert.Single(Events.Entries);
            Assert.Null(entry.Exception);
            Assert.Equal(method, entry.Fields["Method"]);
            Assert.Equal(exceptionType, entry.Fields["ExceptionType"]);
            using var normal = JsonDocument.Parse(entry.Wire);
            Assert.Equal(5101, normal.RootElement.GetProperty("EventId").GetInt32());
            Assert.Equal("ERROR", normal.RootElement.GetProperty("severity").GetString());
            Assert.Equal(JsonValueKind.Null, normal.RootElement.GetProperty("Exception").ValueKind);
            var state = normal.RootElement.GetProperty("State");
            Assert.Equal(method, state.GetProperty("Method").GetString());
            Assert.Equal(exceptionType, state.GetProperty("ExceptionType").GetString());
            var log = new LogEntry<IReadOnlyDictionary<string, object?>>(LogLevel.Error, "Controlled.Dependency", new EventId(5101),
                entry.Fields, null, (_, _) => "private-rendered-sentinel");
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(log, null, writer);
            using var alternate = JsonDocument.Parse(writer.ToString());
            Assert.Equal(method, alternate.RootElement.GetProperty("Method").GetString());
            Assert.Equal(JsonValueKind.Null, alternate.RootElement.GetProperty("exceptionType").ValueKind);
            if (exceptionType is not null) Assert.Equal(exceptionType, alternate.RootElement.GetProperty("ExceptionType").GetString());
            else Assert.False(alternate.RootElement.TryGetProperty("ExceptionType", out _));
            foreach (var output in new[] { entry.Wire, writer.ToString(), JsonSerializer.Serialize(entry.Fields) })
            {
                Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
                Assert.DoesNotContain("dependency.example.invalid", output, StringComparison.Ordinal);
                Assert.DoesNotContain(nameof(CustomerAccountSecretException), output, StringComparison.Ordinal);
            }
            return entry;
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

    private sealed class SingleRetryHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = await base.SendAsync(request, cancellationToken);
            if (first.StatusCode != HttpStatusCode.ServiceUnavailable) return first;
            first.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.BadRequest;
        public Exception? Failure { get; set; }
        public bool Retry { get; set; }
        public bool Block { get; set; }
        public int Calls { get; private set; }
        public HttpResponseMessage? Response { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("Bearer private-auth-sentinel", request.Headers.Authorization?.ToString());
            if (Failure is not null) throw Failure;
            if (Block)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            Response = new(Retry && Calls == 1 ? HttpStatusCode.ServiceUnavailable : Status)
            { Content = new StringContent("private-response-sentinel"), RequestMessage = request };
            return Response;
        }
    }

    private sealed class Events : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public MalievCloudJsonConsoleFormatter Formatter { get; set; } = null!;
        public bool Throw { get; set; }
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, this);
        public void Dispose() { }

        private sealed class Capture(string category, Events owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 5101 || eventId.Name != "DependencyRequestFailure") return;
                var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                using var writer = new StringWriter();
                var log = new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter);
                owner.Formatter.Write(log, null, writer);
                owner.Entries.Enqueue(new(fields, writer.ToString(), exception));
                if (owner.Throw) throw new IOException("private-provider-sentinel");
            }
        }
    }
}
