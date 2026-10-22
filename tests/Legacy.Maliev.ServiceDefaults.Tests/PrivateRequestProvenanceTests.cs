using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests;

/// <summary>Exercises provenance through actual standard-middleware DI and the existing request catch.</summary>
public sealed class PrivateRequestProvenanceTests
{
    private const string Canary = "private-request-provenance-canary";

    /// <summary>Keeps response mapping, severity and event count while optionally capturing a real throw site.</summary>
    /// <param name="enabled">Whether the existing standard-middleware catch captures provenance.</param>
    /// <param name="kind">The controlled exception mapping case.</param>
    /// <param name="status">The unchanged expected HTTP status.</param>
    /// <returns>A task completing after emitted JSON and the public response are checked.</returns>
    [Theory]
    [InlineData(false, 0, 500)]
    [InlineData(true, 0, 500)]
    [InlineData(false, 1, 400)]
    [InlineData(true, 1, 400)]
    [InlineData(false, 2, 404)]
    [InlineData(true, 2, 404)]
    public async Task ActualStandardMiddlewareKeepsResponseAndEventContract(bool enabled, int kind, int status)
    {
        await using var fixture = await Fixture.CreateAsync(enabled);
        var failure = await CaptureFailureAsync(kind);
        fixture.Failure = failure;

        using var response = await fixture.Client.GetAsync("/" + Canary + "?token=" + Canary);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Canary, body, StringComparison.Ordinal);
        using var publicJson = JsonDocument.Parse(body);
        Assert.Equal(status, publicJson.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, publicJson.RootElement.GetProperty("details").ValueKind);
        var record = Assert.Single(fixture.Events.Records);
        Assert.Equal(status == 404 ? LogLevel.Debug : LogLevel.Critical, record.Level);
        Assert.Equal(default(EventId), record.Id);
        Assert.Null(record.Exception);
        Assert.Equal(status, record.Fields["StatusCode"]);
        Assert.Equal("UnhandledRequestFailure", record.Fields["EventName"]);
        Assert.Equal(new[] { "EventName", "Service", "Method", "Path", "StatusCode", "ExceptionType",
            "IncidentId", "OccurredAtUtc", "{OriginalFormat}" }, record.Fields.Keys);
        Assert.DoesNotContain(Canary, record.Message, StringComparison.Ordinal);
        if (status == 404)
        {
            Assert.Equal(string.Empty, record.Json);
            return;
        }
        using var json = JsonDocument.Parse(record.Json);
        Assert.DoesNotContain(Canary, record.Json, StringComparison.Ordinal);
        if (enabled)
        {
            Assert.Equal(failure.GetType().FullName, json.RootElement.GetProperty("exceptionType").GetString());
            Assert.Equal(typeof(ArgumentException).FullName, json.RootElement.GetProperty("innerExceptionType").GetString());
            var target = Assert.IsAssignableFrom<System.Reflection.MethodBase>(failure.TargetSite);
            Assert.Equal(target.DeclaringType?.FullName + "." + target.Name,
                json.RootElement.GetProperty("sourceLocation").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceLocation").ValueKind);
        }
    }

    /// <summary>Leaves the already-started response and existing warning behavior intact.</summary>
    /// <param name="enabled">Whether provenance is captured at the original catch.</param>
    /// <returns>A task completing after the unchanged body, status and primary event are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedResponseIsNotReplacedByProvenance(bool enabled)
    {
        await using var fixture = await Fixture.CreateAsync(enabled);
        fixture.Failure = await CaptureFailureAsync(0);
        fixture.StartResponse = true;

        using var response = await fixture.Client.GetAsync("/started");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Controlled prefix", await response.Content.ReadAsStringAsync());
        Assert.Equal(200, Assert.Single(fixture.Events.Records).Fields["StatusCode"]);
        Assert.Equal(1, fixture.WarningCount);
    }

    /// <summary>Does not add duplicate health failures and preserves successful-response reset.</summary>
    /// <param name="enabled">Whether provenance is selected on the existing exception middleware.</param>
    /// <returns>A task completing after repeated failure, recovery and recurrence are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedHealthFailureOwnershipAndResetRemainUnchanged(bool enabled)
    {
        await using var fixture = await Fixture.CreateAsync(enabled);
        fixture.Status = 503;
        for (int index = 0; index < 3; index++)
        {
            using var response = await fixture.Client.GetAsync("/fixture/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        Assert.Single(fixture.Events.Records);
        fixture.Status = 200;
        using (var healthy = await fixture.Client.GetAsync("/fixture/readiness"))
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Single(fixture.Events.Records);
        fixture.Status = 503;
        using (var failed = await fixture.Client.GetAsync("/fixture/readiness"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(2, fixture.Events.Records.Count);
        Assert.All(fixture.Events.Records, record =>
        {
            Assert.Equal("HealthProbeFailure", record.Fields["EventName"]);
            Assert.Null(record.Exception);
            using var json = JsonDocument.Parse(record.Json);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceLocation").ValueKind);
        });
    }

    /// <summary>Preserves the exact caller-cancellation exception at the unchanged direct middleware API.</summary>
    /// <param name="enabled">Whether provenance was explicitly selected.</param>
    /// <returns>A task completing after cancellation identity and quiet event ownership are checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationIsNotConvertedToProvenanceFailure(bool enabled)
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var expected = new OperationCanceledException(caller.Token);
        using var events = new PrivateTerminalProvenanceTests.ProvenanceRecorder();
        using var factory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(events));
        var middleware = new ExceptionHandlingMiddleware(_ => throw expected,
            factory.CreateLogger<ExceptionHandlingMiddleware>(), new Environment(),
            new MiddlewareOptions { IncludePrivateFailureProvenance = enabled });
        var context = new DefaultHttpContext { RequestAborted = caller.Token };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Same(expected, actual);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        Assert.Empty(events.Records);
    }

    /// <summary>Documents that the original constructor and ordinary default options do not select provenance.</summary>
    [Fact]
    public void ExistingMiddlewareOptionsKeepProvenanceDisabled()
    {
        Assert.False(new MiddlewareOptions().IncludePrivateFailureProvenance);
        Assert.NotNull(typeof(ExceptionHandlingMiddleware).GetConstructor(
            [typeof(RequestDelegate), typeof(ILogger<ExceptionHandlingMiddleware>), typeof(IHostEnvironment)]));
    }

    /// <summary>Preserves the original public constructor's explicit RequestDelegate factory activation.</summary>
    /// <returns>A task completing after factory creation and the default private response contract are checked.</returns>
    [Fact]
    public async Task OriginalCreateFactoryContractRemainsUnambiguous()
    {
        using var events = new PrivateTerminalProvenanceTests.ProvenanceRecorder();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(events));
        services.AddSingleton<IHostEnvironment>(new Environment());
        using var provider = services.BuildServiceProvider();
        var create = ActivatorUtilities.CreateFactory(typeof(ExceptionHandlingMiddleware), [typeof(RequestDelegate)]);
        RequestDelegate next = _ => throw new Exception(Canary);

        var middleware = Assert.IsType<ExceptionHandlingMiddleware>(create(provider, [next]));

        Assert.Single(typeof(ExceptionHandlingMiddleware).GetConstructors());
        await AssertDefaultActivationAsync(middleware.InvokeAsync, events);
    }

    /// <summary>Preserves bare UseMiddleware activation for service providers without constructor-discovery support.</summary>
    /// <returns>A task completing after the real middleware pipeline and its default response are checked.</returns>
    [Fact]
    public async Task BareUseMiddlewareWorksWithoutIsServiceSupport()
    {
        using var events = new PrivateTerminalProvenanceTests.ProvenanceRecorder();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(events));
        services.AddSingleton<IHostEnvironment>(new Environment());
        using var provider = services.BuildServiceProvider();
        var wrapper = new WithoutIsService(provider);
        Assert.NotNull(provider.GetService<IServiceProviderIsService>());
        Assert.Null(wrapper.GetService(typeof(IServiceProviderIsService)));
        var app = new ApplicationBuilder(wrapper);
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.Run(_ => throw new Exception(Canary));

        var pipeline = app.Build();

        await AssertDefaultActivationAsync(pipeline, events);
    }

    private static async Task AssertDefaultActivationAsync(RequestDelegate pipeline,
        PrivateTerminalProvenanceTests.ProvenanceRecorder events)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await pipeline(context);

        Assert.Equal(500, context.Response.StatusCode);
        var record = Assert.Single(events.Records);
        Assert.Equal(LogLevel.Critical, record.Level);
        Assert.Null(record.Exception);
        using var json = JsonDocument.Parse(record.Json);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceLocation").ValueKind);
        Assert.DoesNotContain(Canary, record.Json, StringComparison.Ordinal);
        body.Position = 0;
        using var response = await JsonDocument.ParseAsync(body);
        Assert.Equal(500, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("details").ValueKind);
    }

    private sealed class WithoutIsService(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService)
            ? null : inner.GetService(serviceType);
    }

    private static async Task<Exception> CaptureFailureAsync(int kind)
    {
        try
        {
            await Task.Yield();
            var inner = new ArgumentException(Canary);
            if (kind == 1) throw new ArgumentException(Canary, inner);
            if (kind == 2) throw new KeyNotFoundException(Canary, inner);
            throw new Exception(Canary, inner);
        }
        catch (Exception failure) { return failure; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication app;
        public HttpClient Client { get; }
        public PrivateTerminalProvenanceTests.ProvenanceRecorder Events { get; }
        public Exception? Failure { get; set; }
        public int Status { get; set; } = 200;
        public bool StartResponse { get; set; }
        public int WarningCount { get; private set; }

        private Fixture(WebApplication app, PrivateTerminalProvenanceTests.ProvenanceRecorder events)
        {
            this.app = app;
            Events = events;
            Client = app.GetTestClient();
        }

        public static async Task<Fixture> CreateAsync(bool enabled)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ApplicationName = typeof(PrivateRequestProvenanceTests).Assembly.GetName().Name
            });
            builder.WebHost.UseTestServer();
            builder.AddStandardMiddleware(options => options.IncludePrivateFailureProvenance = enabled);
            builder.AddPrivateRequestObservation("fixture");
            var events = new PrivateTerminalProvenanceTests.ProvenanceRecorder();
            builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(events)
                .AddFilter<PrivateTerminalProvenanceTests.ProvenanceRecorder>(
                    "Maliev.Aspire.ServiceDefaults.Middleware.ExceptionHandlingMiddleware", LogLevel.Trace);
            var app = builder.Build();
            Fixture? fixture = null;
            try
            {
                app.UseStandardMiddleware();
                app.Run(async context =>
                {
                    var current = fixture ?? throw new InvalidOperationException("Fixture must exist before requests.");
                    if (current.StartResponse) await context.Response.WriteAsync("Controlled prefix");
                    if (current.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                    context.Response.StatusCode = current.Status;
                });
                await app.StartAsync();
                var started = new Fixture(app, events);
                fixture = started;
                events.OnWarning = () => started.WarningCount++;
                return started;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { Client.Dispose(); }
            finally { await app.DisposeAsync(); }
        }
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ControlledService";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
