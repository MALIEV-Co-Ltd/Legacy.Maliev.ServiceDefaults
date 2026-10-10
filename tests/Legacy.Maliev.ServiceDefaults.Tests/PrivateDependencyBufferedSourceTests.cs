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

// Uses real HttpClient native buffering after the selected factory handler returns a response.
public sealed class PrivateDependencyBufferedSourceTests
{
    [Theory]
    [InlineData(false, 200, 0)]
    [InlineData(true, 200, 0)]
    [InlineData(false, 400, 0)]
    [InlineData(true, 400, 0)]
    [InlineData(false, 503, 0)]
    [InlineData(true, 503, 0)]
    [InlineData(false, 200, 1)]
    [InlineData(true, 200, 1)]
    [InlineData(false, 400, 1)]
    [InlineData(true, 400, 1)]
    [InlineData(false, 503, 1)]
    [InlineData(true, 503, 1)]
    [InlineData(false, 200, 2)]
    [InlineData(true, 200, 2)]
    [InlineData(false, 400, 2)]
    [InlineData(true, 400, 2)]
    [InlineData(false, 503, 2)]
    [InlineData(true, 503, 2)]
    [InlineData(false, 200, 3)]
    [InlineData(true, 200, 3)]
    [InlineData(false, 400, 3)]
    [InlineData(true, 400, 3)]
    [InlineData(false, 503, 3)]
    [InlineData(true, 503, 3)]
    public async Task Generic_native_buffering_failure_owns_actual_outer_exception_once(bool typed, int status, int kind)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        Exception failure = kind switch
        {
            0 => new InvalidOperationException("private-buffer-sentinel"),
            1 => new IOException("private-buffer-sentinel"),
            2 => new HttpRequestException("private-buffer-sentinel", null, HttpStatusCode.BadGateway),
            _ => new CustomerContentSecretException()
        };
        fixture.Transport.Failure = failure;
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        var actual = await Record.ExceptionAsync(() => Send(fixture.Client, request, observation));
        if (kind == 1)
        {
            var native = Assert.IsType<HttpRequestException>(actual);
            Assert.Same(failure, native.InnerException);
        }
        else Assert.Same(failure, actual);
        Assert.True(observation.WasObserved);
        fixture.AssertFailure(kind == 3 ? "UnknownException" : Assert.IsAssignableFrom<Exception>(actual).GetType().Name, null);
        Assert.Equal(1, fixture.Transport.Calls);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task Status_observation_waits_for_real_buffering_completion(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Block = true;
        using var caller = new CancellationTokenSource();
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        var pending = Send(fixture.Client, request, observation, caller.Token);
        try
        {
            var content = await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Events.Entries);
            Assert.False(observation.WasObserved);
            content.Release.TrySetResult();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(fixture.Transport.Responses.Single(), response);
            Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
            Assert.True(observation.WasObserved);
            fixture.AssertFailure(null, status);
            AssertClear(request);
        }
        finally
        {
            fixture.Transport.ReleaseAll();
            caller.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_buffering_preserves_original_response_and_stays_quiet(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = HttpStatusCode.OK;
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        using var response = await Send(fixture.Client, request, observation);
        Assert.Same(fixture.Transport.Responses.Single(), response);
        Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Transport.Contents.Single().Serializations);
        Assert.Empty(fixture.Events.Entries);
        Assert.False(observation.WasObserved);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, 200, 0)]
    [InlineData(true, 200, 0)]
    [InlineData(false, 400, 0)]
    [InlineData(true, 400, 0)]
    [InlineData(false, 200, 1)]
    [InlineData(true, 200, 1)]
    [InlineData(false, 400, 1)]
    [InlineData(true, 400, 1)]
    [InlineData(false, 200, 2)]
    [InlineData(true, 200, 2)]
    [InlineData(false, 400, 2)]
    [InlineData(true, 400, 2)]
    public async Task Caller_cancel_pending_and_disposal_during_buffering_remain_quiet(bool typed, int status, int cancellation)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Block = true;
        using var caller = new CancellationTokenSource();
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        var pending = Send(fixture.Client, request, observation, caller.Token);
        try
        {
            await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancellation == 0) caller.Cancel();
            else if (cancellation == 1) fixture.Client.CancelPendingRequests();
            else fixture.Client.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(fixture.Events.Entries);
            Assert.False(observation.WasObserved);
            AssertClear(request);
        }
        finally
        {
            caller.Cancel();
            fixture.Transport.ReleaseAll();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public async Task Body_deadline_does_not_claim_handler_cancellation_provenance(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        fixture.Client.Timeout = TimeSpan.FromMilliseconds(100);
        fixture.Transport.Block = true;
        fixture.Transport.Status = (HttpStatusCode)status;
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(fixture.Client, request, observation)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(fixture.Events.Entries);
        Assert.False(observation.WasObserved);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public async Task Forged_timeout_shape_from_body_is_not_native_handler_deadline_proof(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Failure = new TaskCanceledException("private-forged-sentinel",
            new TimeoutException("private-timeout-sentinel", new OperationCanceledException("private-inner-sentinel")));
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => Send(fixture.Client, request, observation)));
        Assert.Empty(fixture.Events.Entries);
        Assert.False(observation.WasObserved);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, 200, 0)]
    [InlineData(true, 200, 0)]
    [InlineData(false, 503, 0)]
    [InlineData(true, 503, 0)]
    [InlineData(false, 200, 1)]
    [InlineData(true, 200, 1)]
    [InlineData(false, 503, 1)]
    [InlineData(true, 503, 1)]
    [InlineData(false, 200, 2)]
    [InlineData(true, 200, 2)]
    [InlineData(false, 503, 2)]
    [InlineData(true, 503, 2)]
    public async Task Unselected_and_existing_modes_keep_buffering_ownership_unchanged(bool typed, int status, int mode)
    {
        using var fixture = new Fixture(typed, mode);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Failure = new InvalidOperationException("private-buffer-sentinel");
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => Send(fixture.Client, request, observation)));
        if (mode != 0 && status == 503)
        {
            var entry = Assert.Single(fixture.Events.Entries);
            Assert.Equal(503, entry.Fields["StatusCode"]);
            Assert.False(entry.Fields.ContainsKey("ExceptionType"));
            Assert.True(observation.WasObserved);
        }
        else
        {
            Assert.Empty(fixture.Events.Entries);
            Assert.False(observation.WasObserved);
        }
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public async Task Headers_read_and_later_body_failure_remain_outside_buffered_send(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Failure = new InvalidOperationException("private-buffer-sentinel");
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        using var response = await fixture.Client.SendWithPrivateFailureObservationAsync(request, CancellationToken.None, observation);
        AssertClear(request);
        Assert.Equal(0, fixture.Transport.Contents.Single().Serializations);
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => response.Content.ReadAsStringAsync()));
        if (status == 400) fixture.AssertFailure(null, 400);
        else Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    public async Task Direct_send_is_not_silently_enrolled_in_outer_buffering_observation(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Status = (HttpStatusCode)status;
        fixture.Transport.Failure = new InvalidOperationException("private-buffer-sentinel");
        using var request = Request();
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => fixture.Client.SendAsync(request)));
        if (status == 400) fixture.AssertFailure(null, 400);
        else Assert.Empty(fixture.Events.Entries);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Ownership_precedes_throwing_sink_and_late_caller_cancellation(bool typed, bool throwingSink)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Failure = new InvalidOperationException("private-buffer-sentinel");
        fixture.Events.Throw = throwingSink;
        using var caller = new CancellationTokenSource();
        using var request = Request();
        var observation = new PrivateDependencyFailureObservation();
        bool claimedAtSink = false;
        fixture.Events.OnFailure = () => { claimedAtSink = observation.WasObserved; caller.Cancel(); };
        Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => Send(fixture.Client, request, observation, caller.Token)));
        Assert.True(claimedAtSink);
        Assert.True(observation.WasObserved);
        Assert.True(caller.IsCancellationRequested);
        fixture.AssertFailure("InvalidOperationException", null);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Native_buffering_follows_final_real_resilience_response_without_replaying_content_failure(bool typed, bool bufferFails)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.RecoverFirstResponse = true;
        if (bufferFails) fixture.Transport.Failure = new InvalidOperationException("private-buffer-sentinel");
        using var request = Request();
        request.Method = HttpMethod.Get;
        var observation = new PrivateDependencyFailureObservation();
        var pending = Send(fixture.Client, request, observation);
        if (bufferFails)
        {
            Assert.Same(fixture.Transport.Failure, await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10))));
            fixture.AssertFailure("InvalidOperationException", null, "GET");
        }
        else
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(fixture.Transport.Responses.Last(), response);
            Assert.Empty(fixture.Events.Entries);
        }
        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Equal(0, fixture.Transport.Contents.First().Serializations);
        Assert.Equal(1, fixture.Transport.Contents.Last().Serializations);
        AssertClear(request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_sends_have_independent_deferred_status_and_cleanup(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Block = true;
        fixture.Transport.Status = HttpStatusCode.BadRequest;
        using var caller = new CancellationTokenSource();
        using var first = Request();
        using var second = Request();
        var one = new PrivateDependencyFailureObservation();
        var two = new PrivateDependencyFailureObservation();
        var pendingOne = Send(fixture.Client, first, one, caller.Token);
        var pendingTwo = Send(fixture.Client, second, two, caller.Token);
        try
        {
            await fixture.Transport.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.Transport.Calls);
            Assert.Empty(fixture.Events.Entries);
            fixture.Transport.ReleaseAll();
            using var responseOne = await pendingOne.WaitAsync(TimeSpan.FromSeconds(5));
            using var responseTwo = await pendingTwo.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(responseOne, responseTwo);
            Assert.True(one.WasObserved);
            Assert.True(two.WasObserved);
            Assert.Equal(2, fixture.Events.Entries.Count);
            Assert.All(fixture.Events.Entries, entry => Assert.Equal(400, entry.Fields["StatusCode"]));
            AssertClear(first);
            AssertClear(second);
        }
        finally
        {
            fixture.Transport.ReleaseAll();
            caller.Cancel();
            try { await Task.WhenAll(pendingOne, pendingTwo).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request,
        PrivateDependencyFailureObservation observation, CancellationToken caller = default) =>
        client.SendWithPrivateFailureObservationAsync(request, caller, observation, HttpCompletionOption.ResponseContentRead);

    private static HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "orders/private-route-sentinel?token=private-query-sentinel");
        request.Headers.Authorization = new("Bearer", "private-auth-sentinel");
        return request;
    }

    private static void AssertClear(HttpRequestMessage request) => Assert.DoesNotContain(request.Options,
        option => option.Key == "Maliev.PrivateDependencyObservationContext" && option.Value is not null);

    private sealed class CustomerContentSecretException() : InvalidOperationException("private-buffer-sentinel") { }
    private sealed class SourceClient(HttpClient client) { public HttpClient Client { get; } = client; }
    private sealed record Entry(IReadOnlyDictionary<string, object?> Fields, string Wire, Exception? Exception);

    private sealed class Fixture : IDisposable
    {
        public Transport Transport { get; } = new();
        public Events Events { get; } = new();
        public IHost Host { get; }
        public HttpClient Client { get; }

        public Fixture(bool typed, int mode = 3)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.AddServiceDefaults();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Events);
            var registration = typed
                ? builder.Services.AddHttpClient<SourceClient>("BufferedSource", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"))
                : builder.Services.AddHttpClient("BufferedSource", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"));
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (mode == 1) registration.AddPrivateFailureObservation("ControlledDependency");
            else if (mode == 2) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            else if (mode == 3) registration.AddPrivateFailureSourceObservation("ControlledDependency");
            Host = builder.Build();
            try
            {
                Assert.Equal(MalievCloudJsonConsoleFormatter.FormatterName,
                    Host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
                Events.Formatter = Assert.Single(Host.Services.GetServices<ConsoleFormatter>().OfType<MalievCloudJsonConsoleFormatter>());
                Client = typed ? Host.Services.GetRequiredService<SourceClient>().Client
                    : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BufferedSource");
            }
            catch
            {
                try { Host.Dispose(); }
                finally { Transport.Dispose(); }
                throw;
            }
        }

        public void AssertFailure(string? type, int? status, string method = "POST")
        {
            var entry = Assert.Single(Events.Entries);
            Assert.Null(entry.Exception);
            Assert.Equal(method, entry.Fields["Method"]);
            Assert.Equal(type, entry.Fields["ExceptionType"]);
            Assert.Equal(method == "GET" ? "Orders.Get" : "Orders.Post", entry.Fields["Operation"]);
            if (status is { } value) Assert.Equal(value, entry.Fields["StatusCode"]);
            else Assert.False(entry.Fields.ContainsKey("StatusCode"));
            using var normal = JsonDocument.Parse(entry.Wire);
            Assert.Equal("ERROR", normal.RootElement.GetProperty("severity").GetString());
            Assert.Equal(5101, normal.RootElement.GetProperty("EventId").GetInt32());
            Assert.Equal(JsonValueKind.Null, normal.RootElement.GetProperty("Exception").ValueKind);
            Assert.Equal(type, normal.RootElement.GetProperty("State").GetProperty("ExceptionType").GetString());
            var log = new LogEntry<IReadOnlyDictionary<string, object?>>(LogLevel.Error, "Controlled.Dependency", new EventId(5101),
                entry.Fields, null, (_, _) => "private-rendered-sentinel");
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(log, null, writer);
            using var alternate = JsonDocument.Parse(writer.ToString());
            Assert.Equal(method, alternate.RootElement.GetProperty("Method").GetString());
            if (type is not null) Assert.Equal(type, alternate.RootElement.GetProperty("ExceptionType").GetString());
            foreach (var output in new[] { entry.Wire, writer.ToString(), JsonSerializer.Serialize(entry.Fields) })
            {
                Assert.DoesNotContain("private-", output, StringComparison.Ordinal);
                Assert.DoesNotContain("dependency.example.invalid", output, StringComparison.Ordinal);
                Assert.DoesNotContain(nameof(CustomerContentSecretException), output, StringComparison.Ordinal);
            }
        }

        public void Dispose()
        {
            Transport.ReleaseAll();
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
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Failure { get; set; }
        public bool Block { get; set; }
        public bool RecoverFirstResponse { get; set; }
        private int calls;
        private int buffersEntered;
        public int Calls => Volatile.Read(ref calls);
        public ConcurrentQueue<HttpResponseMessage> Responses { get; } = new();
        public ConcurrentQueue<Content> Contents { get; } = new();
        public TaskCompletionSource<Content> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            Assert.Equal("Bearer private-auth-sentinel", request.Headers.Authorization?.ToString());
            var content = new Content(this, Failure, Block);
            Contents.Enqueue(content);
            var response = new HttpResponseMessage(RecoverFirstResponse && call == 1 ? HttpStatusCode.ServiceUnavailable : Status)
            { Content = content, RequestMessage = request };
            Responses.Enqueue(response);
            return Task.FromResult(response);
        }

        public void ReleaseAll() { foreach (var content in Contents) content.Release.TrySetResult(); }

        public void BufferEntered(Content content)
        {
            Entered.TrySetResult(content);
            if (Interlocked.Increment(ref buffersEntered) == 2) BothEntered.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseAll();
                foreach (var response in Responses) response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Content(Transport owner, Exception? failure, bool block) : HttpContent
    {
        public int Serializations { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Serializations++;
            owner.BufferEntered(this);
            if (block) await Release.Task.WaitAsync(cancellationToken);
            if (failure is not null) throw failure;
            var bytes = System.Text.Encoding.UTF8.GetBytes("private-response-sentinel");
            await stream.WriteAsync(bytes, cancellationToken);
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class Events : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public MalievCloudJsonConsoleFormatter Formatter { get; set; } = null!;
        public bool Throw { get; set; }
        public Action? OnFailure { get; set; }
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
                var entry = new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter);
                owner.Formatter.Write(entry, null, writer);
                owner.Entries.Enqueue(new(fields, writer.ToString(), exception));
                owner.OnFailure?.Invoke();
                if (owner.Throw) throw new IOException("private-provider-sentinel");
            }
        }
    }
}
