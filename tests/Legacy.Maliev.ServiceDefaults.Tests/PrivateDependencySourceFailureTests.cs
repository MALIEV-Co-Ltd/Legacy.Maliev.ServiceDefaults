using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Original 3d650628/9e51e6 outbound failure predicate; safe metadata adaptation remains explicit.
public sealed class PrivateDependencySourceFailureTests
{
    [Theory]
    [InlineData(false, 302)]
    [InlineData(true, 302)]
    [InlineData(false, 304)]
    [InlineData(true, 304)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    [InlineData(false, 401)]
    [InlineData(true, 401)]
    [InlineData(false, 404)]
    [InlineData(true, 404)]
    [InlineData(false, 409)]
    [InlineData(true, 409)]
    [InlineData(false, 429)]
    [InlineData(true, 429)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task Explicit_source_mode_records_non_success_without_changing_response(bool typed, int status)
    {
        using var fixture = new Fixture(typed, 3);
        fixture.Transport.Status = (HttpStatusCode)status;
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();

        using var response = await fixture.Client.SendWithPrivateFailureObservationAsync(request, CancellationToken.None, observation);

        Assert.Same(fixture.Transport.Response, response);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        Assert.True(observation.WasObserved);
        fixture.AssertFailure(status);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 204)]
    [InlineData(true, 204)]
    public async Task Source_mode_keeps_success_quiet(bool typed, int status)
    {
        using var fixture = new Fixture(typed, 3);
        fixture.Transport.Status = (HttpStatusCode)status;
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Source_mode_records_unexpected_exception_without_export_or_replacement(bool typed, int kind)
    {
        using var fixture = new Fixture(typed, 3);
        Exception failure = kind switch
        {
            0 => new InvalidOperationException("private-exception-sentinel"),
            1 => new IOException("private-exception-sentinel"),
            _ => new ArgumentException("private-exception-sentinel")
        };
        failure.Data["Customer"] = "private-data-sentinel";
        fixture.Transport.Failure = failure;
        using var request = Request();

        var actual = await Record.ExceptionAsync(() => fixture.Client.SendAsync(request));

        Assert.Same(failure, actual);
        fixture.AssertFailure(null);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Existing_modes_retain_4xx_and_unexpected_exception_silence(bool typed, int mode)
    {
        using var fixture = new Fixture(typed, mode);
        fixture.Transport.Status = HttpStatusCode.BadRequest;
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        Assert.Empty(fixture.Events.Entries);
        fixture.Transport.Failure = new InvalidOperationException("private-exception-sentinel");
        using var second = Request();
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => fixture.Client.SendAsync(second)));
        Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unselected_client_is_not_activated(bool typed)
    {
        using var fixture = new Fixture(typed, 0);
        fixture.Transport.Status = HttpStatusCode.BadRequest;
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request);
        Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_caller_cancellation_remains_quiet(bool typed)
    {
        using var fixture = new Fixture(typed, 3);
        fixture.Transport.Block = true;
        using var caller = new CancellationTokenSource();
        using var request = Request();
        var pending = fixture.Client.SendAsync(request, caller.Token);
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
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Observer_stays_outside_retry_and_records_only_terminal_outcome(bool typed, bool recovered)
    {
        using var fixture = new Fixture(typed, 3, retry: true);
        fixture.Transport.Status = recovered ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        Assert.Equal(2, fixture.Transport.Calls);
        if (recovered) Assert.Empty(fixture.Events.Entries);
        else fixture.AssertFailure(400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_source_selection_and_throwing_sink_preserve_single_response(bool typed)
    {
        using var fixture = new Fixture(typed, 3, repeat: true);
        fixture.Events.Throw = true;
        fixture.Transport.Status = HttpStatusCode.BadRequest;
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request);
        Assert.Same(fixture.Transport.Response, response);
        fixture.AssertFailure(400);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Different_observation_modes_fail_closed_in_either_registration_order(bool sourceFirst, int existingMode)
    {
        var registration = new ServiceCollection().AddHttpClient("ModeProof");
        if (sourceFirst)
        {
            registration.AddPrivateFailureSourceObservation("ControlledDependency");
            Assert.Throws<InvalidOperationException>(() => Select(registration, existingMode));
        }
        else
        {
            Select(registration, existingMode);
            Assert.Throws<InvalidOperationException>(() => registration.AddPrivateFailureSourceObservation("ControlledDependency"));
        }
    }

    [Fact]
    public void Different_source_dependency_identity_fails_closed()
    {
        var registration = new ServiceCollection().AddHttpClient("IdentityProof");
        registration.AddPrivateFailureSourceObservation("ControlledDependency");
        Assert.Throws<InvalidOperationException>(() => registration.AddPrivateFailureSourceObservation("OtherDependency"));
    }

    private static HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "orders/private-route-sentinel?token=private-query-sentinel")
        { Content = new StringContent("private-body-sentinel") };
        request.Headers.Authorization = new("Bearer", "private-auth-sentinel");
        return request;
    }

    private static void Select(IHttpClientBuilder registration, int mode)
    {
        if (mode == 1) registration.AddPrivateFailureObservation("ControlledDependency");
        else if (mode == 2) registration.AddPrivateFailureOperationObservation("ControlledDependency");
        else if (mode == 3) registration.AddPrivateFailureSourceObservation("ControlledDependency");
    }

    private sealed class SourceClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class Fixture : IDisposable
    {
        public Transport Transport { get; } = new();
        public Events Events { get; } = new();
        public IHost Host { get; }
        public HttpClient Client { get; }

        public Fixture(bool typed, int mode, bool retry = false, bool repeat = false)
        {
            Transport.Retry = retry;
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.AddProvider(Events);
            var registration = typed
                ? builder.Services.AddHttpClient<SourceClient>("SourceProof", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"))
                : builder.Services.AddHttpClient("SourceProof", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"));
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (retry) registration.AddHttpMessageHandler(() => new SingleRetryHandler());
            Select(registration, mode);
            if (repeat) Select(registration, mode);
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<SourceClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SourceProof");
        }

        public void AssertFailure(int? status)
        {
            var entry = Assert.Single(Events.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal("ControlledDependency", entry.Fields["Dependency"]);
            Assert.Equal("Orders.Post", entry.Fields["Operation"]);
            if (status is { } number) Assert.Equal(number, entry.Fields["StatusCode"]);
            else Assert.False(entry.Fields.ContainsKey("StatusCode"));
            var log = new LogEntry<IReadOnlyDictionary<string, object?>>(entry.Level, entry.Category, entry.Id,
                entry.Fields, entry.Exception, (_, _) => "private-rendered-sentinel");
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(log, null, writer);
            var wire = writer.ToString();
            Assert.DoesNotContain("private-", wire, StringComparison.Ordinal);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(entry.Fields), StringComparison.Ordinal);
            Assert.DoesNotContain("dependency.example.invalid", wire, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(wire);
            Assert.Equal("Orders.Post", json.RootElement.GetProperty("Operation").GetString());
            Assert.Equal("ERROR", json.RootElement.GetProperty("severity").GetString());
            Assert.Equal(5101, json.RootElement.GetProperty("eventId").GetInt32());
            if (status is { } code) Assert.Equal(code, json.RootElement.GetProperty("StatusCode").GetInt32());
            else Assert.False(json.RootElement.TryGetProperty("StatusCode", out _));
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
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return response;
            response.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.BadRequest;
        public Exception? Failure { get; set; }
        public bool Block { get; set; }
        public bool Retry { get; set; }
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

    private sealed record Entry(LogLevel Level, string Category, EventId Id, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);

    private sealed class Events : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
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
                owner.Entries.Enqueue(new(logLevel, category, eventId, exception, fields));
                if (owner.Throw) throw new IOException("private-provider-sentinel");
            }
        }
    }
}
