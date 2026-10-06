using System.Collections.Concurrent;
using System.Net;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// All arrangements below belong to the test host, not the producer or a service consumer.
internal sealed class PrivateRequestObservationHttpFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _downstreamCalls;
    private int _status = 200;
    private int _syntheticOverwriteCallbacks;
    private int _healthOverwriteCallbacks;
    private int _lateRequestsWithoutEndpoint;
    private int _lateRequestsWithEndpoint;
    private int _startedResponsesBeforeObserver;
    private int _exceptionHandlerCalls;
    private string? _handledExceptionPath;
    private HttpClient? _client;

    private PrivateRequestObservationHttpFixture(WebApplication app,
        PrivateRequestRecordingProvider records, PrivateRequestClock clock)
    {
        _app = app;
        Records = records;
        Clock = clock;
    }

    public HttpClient Client => _client ?? throw new InvalidOperationException("Test host has not started.");
    public PrivateRequestRecordingProvider Records { get; }
    public PrivateRequestClock Clock { get; }
    public int DownstreamCalls => Volatile.Read(ref _downstreamCalls);
    public int SyntheticOverwriteCallbacks => Volatile.Read(ref _syntheticOverwriteCallbacks);
    public int HealthOverwriteCallbacks => Volatile.Read(ref _healthOverwriteCallbacks);
    public int LateRequestsWithoutEndpoint => Volatile.Read(ref _lateRequestsWithoutEndpoint);
    public int LateRequestsWithEndpoint => Volatile.Read(ref _lateRequestsWithEndpoint);
    public int StartedResponsesBeforeObserver => Volatile.Read(ref _startedResponsesBeforeObserver);
    public int ExceptionHandlerCalls => Volatile.Read(ref _exceptionHandlerCalls);
    public string? HandledExceptionPath => _handledExceptionPath;
    public int Status { set => Volatile.Write(ref _status, value); }

    public static async Task<PrivateRequestObservationHttpFixture> CreateAsync(
        string? peer = "127.0.0.1", bool selected = true, bool mapHealth = true,
        bool rewriteHeaders = false, bool repeatRegistration = false,
        bool mapBusinessReadiness = false, bool rewriteSyntheticHeaders = false,
        bool lateRouting = false, bool shortCircuitBeforeLateRouting = false,
        bool startedResponseBeforeObserver = false, bool downstreamExceptionHandler = false,
        bool incidentHeaderOnReturnedFailure = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(PrivateRequestObservationHttpFixture).Assembly.GetName().Name
        });
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
        builder.Configuration["Observability:TracingEnabled"] = "false";
        builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
        builder.Configuration["ForwardedHeaders:KnownProxies:0"] = "203.0.113.7";
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();
        builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
        if (selected)
        {
            builder.AddPrivateRequestObservation("review");
            if (repeatRegistration)
            {
                builder.AddPrivateRequestObservation("review");
            }
        }
        var clock = new PrivateRequestClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        var records = new PrivateRequestRecordingProvider();
        // Keep the native defaults/provider registrations; add one independent recorder.
        builder.Logging.AddProvider(records);
        PrivateRequestObservationHttpFixture? fixture = null;
        builder.Services.AddHealthChecks().AddCheck("controlled-health", () =>
            fixture is not null && Volatile.Read(ref fixture._status) >= 500
                ? HealthCheckResult.Unhealthy()
                : HealthCheckResult.Healthy());
        var app = builder.Build();
        try
        {
            var startedFixture = new PrivateRequestObservationHttpFixture(app, records, clock);
            fixture = startedFixture;
            if (rewriteSyntheticHeaders)
            {
                records.OnFrameworkError = () =>
                {
                    var context = app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                    context.Response.OnStarting(() =>
                    {
                        Interlocked.Increment(ref startedFixture._syntheticOverwriteCallbacks);
                        context.Response.Headers["X-Maliev-Health-Instance"] = "fixture-overwrite";
                        context.Response.Headers["X-Maliev-Diagnostic-Id"] = "fixture-overwrite";
                        context.Response.Headers.CacheControl = "public";
                        return Task.CompletedTask;
                    });
                };
            }
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
                await next(context);
            });
            if (!lateRouting) app.UseRouting();
            else
            {
                app.Use(async (context, next) =>
                {
                    if (context.GetEndpoint() is null)
                        Interlocked.Increment(ref startedFixture._lateRequestsWithoutEndpoint);
                    await next(context);
                });
            }
            if (startedResponseBeforeObserver)
            {
                // Isolate the real observer contract: the standard security/correlation middleware
                // deliberately writes headers and does not support an already-started response.
                app.Use(async (context, next) =>
                {
                    if (HttpMethods.IsGet(context.Request.Method)
                        && context.Request.Path == "/fixture/business")
                    {
                        await context.Response.WriteAsync("Benign started response");
                        if (context.Response.HasStarted)
                            Interlocked.Increment(ref startedFixture._startedResponsesBeforeObserver);
                    }
                    await next(context);
                });
                PrivateRequestObservationPipeline.UseCompletedResponseObserver(app,
                    app.Services.GetRequiredService<PrivateRequestObservationState>());
            }
            else app.UseStandardMiddleware();
            if (lateRouting)
            {
                if (shortCircuitBeforeLateRouting)
                {
                    app.Use(async (context, next) =>
                    {
                        if (HttpMethods.IsGet(context.Request.Method)
                            && context.Request.Path == "/review/readiness")
                        {
                            context.Response.StatusCode = StatusCodes.Status200OK;
                            await context.Response.WriteAsync("Benign pre-routing response");
                            return;
                        }
                        await next(context);
                    });
                }
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    if (context.GetEndpoint() is not null)
                        Interlocked.Increment(ref startedFixture._lateRequestsWithEndpoint);
                    await next(context);
                });
            }
            if (rewriteHeaders)
            {
                app.Use(async (context, next) =>
                {
                    context.Response.OnStarting(() =>
                    {
                        Interlocked.Increment(ref startedFixture._healthOverwriteCallbacks);
                        context.Response.Headers["X-Maliev-Health-Instance"] = "downstream-value";
                        context.Response.Headers.CacheControl = "public";
                        return Task.CompletedTask;
                    });
                    await next(context);
                });
            }
            if (downstreamExceptionHandler)
            {
                app.UseExceptionHandler("/fixture/handled-error");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/fixture/handled-error")
                    {
                        Interlocked.Increment(ref startedFixture._exceptionHandlerCalls);
                        startedFixture._handledExceptionPath = context.Features
                            .Get<IExceptionHandlerPathFeature>()?.Path;
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsync("Benign exception response");
                        return;
                    }
                    try { await next(context); }
                    catch (Exception)
                    {
                        app.Logger.LogCritical("{EventName}", "FixtureOwnedUnhandledFailure");
                        throw;
                    }
                });
            }
            if (mapHealth)
            {
                app.MapDefaultEndpoints("review");
            }
            else if (mapBusinessReadiness)
            {
                app.MapGet("/review/readiness", () => "Business response");
            }
            app.MapMethods("/fixture/{**path}", ["GET", "POST", "HEAD"], context =>
            {
                Interlocked.Increment(ref startedFixture._downstreamCalls);
                if (!startedResponseBeforeObserver)
                    context.Response.StatusCode = Volatile.Read(ref startedFixture._status);
                if (incidentHeaderOnReturnedFailure)
                    context.Response.Headers["X-Incident-Id"] = "fixture-header-only";
                return Task.CompletedTask;
            });
            app.MapGet("/ordinary-failure", (RequestDelegate)(_ =>
                throw new Exception("private-ordinary-exception-sentinel")));
            app.MapGet("/throw/readiness", (RequestDelegate)(_ =>
                throw new Exception("private-ordinary-exception-sentinel")));
            app.MapFallback(context =>
            {
                Interlocked.Increment(ref startedFixture._downstreamCalls);
                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            });
            await app.StartAsync();
            startedFixture._client = app.GetTestClient();
            startedFixture._client.BaseAddress = new Uri("https://localhost");
            records.Clear();
            return startedFixture;
        }
        catch
        {
            try
            {
                fixture?._client?.Dispose();
            }
            finally { await app.DisposeAsync(); }
            throw;
        }
    }

    public async Task<HttpResponseMessage> SendAsync(string path, string method = "GET",
        string[]? nonce = null, string? forwarded = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (nonce is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Maliev-Diagnostic-Id", nonce);
        }
        if (forwarded is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwarded);
        }
        return await Client.SendAsync(request);
    }

    public void EmitNativeControl() => _app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Fixture.Native").LogInformation("Benign native event");

    public void EmitSyntheticFields(object? synthetic, object? diagnosticId)
    {
        var logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fixture.Allowlist");
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["Synthetic"] = synthetic,
            ["DiagnosticId"] = diagnosticId,
            ["Credential"] = "private-credential-sentinel"
        }))
        {
            logger.LogWarning("{EventName}", "AllowlistProbe");
        }
    }

    public void EmitSyntheticContainers(Dictionary<string, object?> state,
        params Dictionary<string, object?>[] scopes)
    {
        var logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fixture.Pair");
        var lifetimes = new Stack<IDisposable>();
        try
        {
            foreach (var scope in scopes)
            {
                var lifetime = logger.BeginScope(scope);
                if (lifetime is not null) lifetimes.Push(lifetime);
            }
            logger.Log(LogLevel.Warning, new EventId(5102), state, null, (_, _) => "Pair probe");
        }
        finally
        {
            while (lifetimes.TryPop(out var lifetime)) lifetime.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _client?.Dispose(); }
        finally { await _app.DisposeAsync(); }
    }
}

internal sealed class PrivateRequestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed record PrivateRequestLog(string Category, LogLevel Level, EventId EventId,
    Dictionary<string, object?> State, string? ExceptionType, string PrivateJson, string NativeJson);

internal sealed class PrivateRequestRecordingProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<PrivateRequestLog> _records = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public Action? OnFrameworkError { get; set; }
    public PrivateRequestLog[] Snapshot => _records.ToArray();
    public PrivateRequestLog[] Failures => Snapshot.Where(record => record.Level >= LogLevel.Warning).ToArray();
    public PrivateRequestLog[] Observations => Failures.Where(record =>
        record.State.GetValueOrDefault("EventName") as string is "HealthProbeFailure" or "HandledOperationFailure").ToArray();
    public void Clear() => _records.Clear();
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
    public void Dispose() { }

    private sealed class Recorder(PrivateRequestRecordingProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
            using var privateOutput = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(in entry, owner._scopes, privateOutput);
            using var nativeOutput = new StringWriter();
            var native = new MalievCloudJsonConsoleFormatter(new FixedConsoleOptions());
            native.Write(in entry, owner._scopes, nativeOutput);
            owner._records.Enqueue(new PrivateRequestLog(category, level, eventId, fields,
                exception?.GetType().FullName, privateOutput.ToString(), nativeOutput.ToString()));
            if (category == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"
                && eventId.Id == 1 && eventId.Name == "UnhandledException")
            {
                owner.OnFrameworkError?.Invoke();
            }
        }
    }

    private sealed class FixedConsoleOptions : IOptionsMonitor<JsonConsoleFormatterOptions>
    {
        public JsonConsoleFormatterOptions CurrentValue { get; } = new()
        {
            IncludeScopes = true,
            UseUtcTimestamp = true,
            TimestampFormat = "O"
        };
        public JsonConsoleFormatterOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<JsonConsoleFormatterOptions, string?> listener) => null;
    }
}
