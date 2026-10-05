using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Only external transport is controlled: cancellation, deadline rewriting and selection are real.
public sealed class PrivateDependencyNativeDeadlineTests
{
    private const string Canary = "synthetic-private-native-deadline-canary";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Native_deadline_records_once_after_real_HttpClient_rewrites_terminal_cancellation(bool typed, bool nestedTimeout)
    {
        using var fixture = new Fixture(typed, nestedTimeout, TimeSpan.FromSeconds(1));
        using var request = Request();
        var pending = SendObservedAsync(fixture.Client, request);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        var timeout = Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Same(fixture.Transport.TerminalCancellation, timeout.InnerException);
        Assert.NotSame(fixture.Transport.TerminalCancellation, failure);
        Assert.True(fixture.Transport.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.Transport.Calls);
        fixture.AssertFailure(null);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Real_pending_or_dispose_cancellation_is_quiet_even_with_transport_forged_timeout(bool typed, bool dispose, bool nestedTimeout)
    {
        using var fixture = new Fixture(typed, nestedTimeout, Timeout.InfiniteTimeSpan);
        using var request = Request();
        var pending = SendObservedAsync(fixture.Client, request);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (dispose) fixture.Client.Dispose();
        else fixture.Client.CancelPendingRequests();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(fixture.Transport.TerminalCancellation, failure);
        Assert.True(fixture.Transport.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_caller_cancellation_preserves_caller_token_and_stays_quiet(bool typed)
    {
        using var fixture = new Fixture(typed, true, Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource();
        using var request = Request();
        var pending = SendObservedAsync(fixture.Client, request, cancellation.Token);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forged_timeout_without_native_cancellation_preserves_exception_and_existing_single_event(bool typed)
    {
        using var fixture = new Fixture(typed, false, Timeout.InfiniteTimeSpan);
        var expected = new TaskCanceledException(Canary, new TimeoutException(Canary));
        fixture.Transport.Failure = expected;
        using var request = Request();

        var actual = await Assert.ThrowsAsync<TaskCanceledException>(() => SendObservedAsync(fixture.Client, request));

        Assert.Same(expected, actual);
        Assert.False(fixture.Transport.Token.IsCancellationRequested);
        fixture.AssertFailure(null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Outer_send_preserves_existing_transport_or_503_event_without_duplication(bool typed, bool transportFailure)
    {
        using var fixture = new Fixture(typed, false, Timeout.InfiniteTimeSpan);
        fixture.Transport.Response = new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(Canary) };
        var expected = new HttpRequestException(Canary);
        if (transportFailure) fixture.Transport.Failure = expected;
        using var request = Request();

        if (transportFailure)
        {
            var actual = await Assert.ThrowsAsync<HttpRequestException>(() => SendObservedAsync(fixture.Client, request));
            Assert.Same(expected, actual);
        }
        else
        {
            using var response = await SendObservedAsync(fixture.Client, request);
            Assert.Same(fixture.Transport.Response, response);
            Assert.Equal(Canary, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(1, fixture.Transport.Calls);
        fixture.AssertFailure(transportFailure ? null : 503);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unselected_helper_or_existing_direct_send_does_not_gain_native_deadline_observation(bool typed, bool direct)
    {
        using var fixture = new Fixture(typed, false, TimeSpan.FromSeconds(1), selected: direct);
        using var request = Request();
        var pending = direct ? fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            : SendObservedAsync(fixture.Client, request);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Empty(fixture.Events.Failures);
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "/orders/" + Canary + "?secret=" + Canary);

    // Until the additive API exists, use the unchanged real send. Missing implementation must
    // fail on the observable event contract at runtime, not on an uncompilable API reference.
    private static Task<HttpResponseMessage> SendObservedAsync(HttpClient client, HttpRequestMessage request, CancellationToken token = default)
    {
        var type = typeof(PrivateFailureConsoleFormatter).Assembly.GetType(
            "Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateDependencyHttpClientExtensions");
        var method = type?.GetMethod("SendWithPrivateFailureObservationAsync", BindingFlags.Public | BindingFlags.Static,
            [typeof(HttpClient), typeof(HttpRequestMessage), typeof(CancellationToken)]);
        if (method is null) return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try { return Assert.IsAssignableFrom<Task<HttpResponseMessage>>(method.Invoke(null, [client, request, token])); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public sealed class NativeClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class Fixture : IDisposable
    {
        public IHost Host { get; }
        public HttpClient Client { get; }
        public Transport Transport { get; }
        public EventProvider Events { get; } = new();

        public Fixture(bool typed, bool nestedTimeout, TimeSpan timeout, bool selected = true)
        {
            Transport = new(nestedTimeout);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Logging.AddProvider(Events);
            Action<HttpClient> configure = client =>
            {
                client.BaseAddress = new Uri("https://native-deadline.example.invalid/");
                client.Timeout = timeout;
            };
            var registration = typed ? builder.Services.AddHttpClient<NativeClient>("NativeDeadline", configure)
                : builder.Services.AddHttpClient("NativeDeadline", configure);
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (selected) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<NativeClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("NativeDeadline");
        }

        public void AssertFailure(int? status)
        {
            var failure = Assert.Single(Events.Failures);
            Assert.Equal(LogLevel.Error, failure.Level);
            Assert.Equal(5101, failure.Id.Id);
            Assert.Equal("DependencyRequestFailure", failure.Id.Name);
            Assert.Equal("Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateDependencyFailureHandler", failure.Category);
            Assert.Null(failure.Exception);
            Assert.Equal("DependencyRequestFailure", failure.Fields["EventName"]);
            Assert.Equal("ControlledDependency", failure.Fields["Dependency"]);
            Assert.Equal("Orders.Get", failure.Fields["Operation"]);
            if (status is { } known) Assert.Equal(known, failure.Fields["StatusCode"]);
            else Assert.False(failure.Fields.ContainsKey("StatusCode"));
            Assert.DoesNotContain(Canary, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, JsonSerializer.Serialize(failure.Fields), StringComparison.Ordinal);
            var entry = new LogEntry<IReadOnlyDictionary<string, object?>>(failure.Level, failure.Category,
                failure.Id, failure.Fields, failure.Exception, (_, _) => failure.Message);
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(entry, null, writer);
            using var wire = JsonDocument.Parse(writer.ToString());
            Assert.Equal("Orders.Get", wire.RootElement.GetProperty("Operation").GetString());
            Assert.DoesNotContain(Canary, writer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("native-deadline.example.invalid", writer.ToString(), StringComparison.Ordinal);
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

    private sealed class Transport(bool nestedTimeout) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public OperationCanceledException? TerminalCancellation { get; private set; }
        public Exception? Failure { get; set; }
        public HttpResponseMessage? Response { get; set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            Entered.TrySetResult();
            if (Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            if (Response is { } response) return response;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException exception)
            {
                TerminalCancellation = nestedTimeout
                    ? new TaskCanceledException(Canary, new TimeoutException(Canary), cancellationToken)
                    : exception;
                ExceptionDispatchInfo.Capture(TerminalCancellation).Throw();
                throw;
            }
            throw new InvalidOperationException("The controlled transport must terminate through real cancellation.");
        }
    }

    private sealed record Failure(LogLevel Level, string Category, EventId Id, string Message,
        Exception? Exception, IReadOnlyDictionary<string, object?> Fields);

    private sealed class EventProvider : ILoggerProvider
    {
        public ConcurrentQueue<Failure> Failures { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Failures);
        public void Dispose() { }
        private sealed class Recorder(string category, ConcurrentQueue<Failure> failures) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (id.Id != 5101) return;
                var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                failures.Enqueue(new(level, category, id, formatter(state, exception), exception, fields));
            }
        }
    }
}
