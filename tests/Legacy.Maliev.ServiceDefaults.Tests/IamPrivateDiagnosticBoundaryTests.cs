using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.Aspire.ServiceDefaults.Logging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Compatibility and producer privacy controls. Only external transport is replaced.
public sealed class IamPrivateDiagnosticBoundaryTests
{
    private const string Permission = "private-permission-sentinel";
    private const string Resource = "private-resource-sentinel";
    private const string ResourceType = "private-type-sentinel";
    // Deliberately synthetic fixture value; never read an ambient or production credential.
    private const string Credential = "fixture-only-iam-live-check-value";
    private const string Header = "X-Maliev-IAM-Live-Check-Key";

    [Theory]
    [InlineData("503", "WARNING", "HttpStatus")]
    [InlineData("malformed", "ERROR", "InvalidJson")]
    [InlineData("throw", "ERROR", "Transport")]
    [InlineData("timeout", "ERROR", "Timeout")]
    [InlineData("unexpected", "ERROR", "Unexpected")]
    [InlineData("transport-cancel", "ERROR", "Cancelled")]
    public async Task ResolvePermissions_FailureFailsClosedAndPrivateOutputRejectsPrincipalAndExceptionText(
        string failure, string severity, string failureKind)
    {
        using var rig = new Rig((_, _) => FaultResponse(failure));
        var principal = UniquePrincipal();
        var result = await rig.Client.GetUserPermissionsAsync(principal);
        Assert.Empty(result);
        var request = AssertRequest(rig, "POST", "/iam/v1/auth/resolve-permissions");
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(new[] { "principalId" }, body.RootElement.EnumerateObject().Select(field => field.Name).ToArray());
        Assert.Equal(principal, body.RootElement.GetProperty("principalId").GetString());
        Assert.False(request.Headers.ContainsKey(Header));
        AssertSafeIam(Assert.Single(rig.Records.Entries), severity, failureKind, principal);
    }

    [Theory]
    [InlineData(false, "503", "WARNING", "HttpStatus")]
    [InlineData(true, "503", "WARNING", "HttpStatus")]
    [InlineData(false, "malformed", "ERROR", "InvalidJson")]
    [InlineData(true, "malformed", "ERROR", "InvalidJson")]
    [InlineData(false, "throw", "ERROR", "Transport")]
    [InlineData(true, "throw", "ERROR", "Transport")]
    [InlineData(false, "transport-cancel", "ERROR", "Cancelled")]
    [InlineData(true, "transport-cancel", "ERROR", "Cancelled")]
    public async Task CheckPermission_FailureDeniesAndPreservesStandardVersusLiveWireContract(
        bool live, string failure, string severity, string failureKind)
    {
        using var rig = new Rig((_, _) => FaultResponse(failure));
        var principal = UniquePrincipal();
        bool result = live
            ? await rig.Client.CheckPermissionLiveAsync(principal, Permission, Resource)
            : await rig.Client.CheckPermissionAsync(principal, Permission, Resource);
        Assert.False(result);
        AssertPermissionRequest(AssertRequest(rig, "POST", "/iam/v1/auth/check-permission"), principal, Permission, live);
        AssertSafeIam(Assert.Single(rig.Records.Entries), severity, failureKind, principal);
    }

    [Theory]
    [InlineData("503", "WARNING", "HttpStatus")]
    [InlineData("malformed", "ERROR", "InvalidJson")]
    [InlineData("throw", "ERROR", "Transport")]
    [InlineData("transport-cancel", "ERROR", "Cancelled")]
    public async Task AuthorizedResources_FailureReturnsNoResourcesAndPrivateOutputRejectsQueryAndExceptionText(
        string failure, string severity, string failureKind)
    {
        using var rig = new Rig((_, _) => FaultResponse(failure));
        var principal = UniquePrincipal();
        var result = await rig.Client.GetAuthorizedResourcesAsync(principal, Permission, ResourceType);
        Assert.Empty(result);
        var request = AssertRequest(rig, "GET",
            $"/iam/v1/auth/authorized-resources?principalId={principal}&permissionId=private-permission-sentinel&resourceType=private-type-sentinel");
        Assert.Null(request.Body);
        Assert.False(request.Headers.ContainsKey(Header));
        AssertSafeIam(Assert.Single(rig.Records.Entries), severity, failureKind, principal);
    }

    [Theory]
    [InlineData("permissions")]
    [InlineData("resources")]
    public async Task LookupSuccess_ReturnsOnlyWireResultsWithoutInventingFailureDiagnostics(string operation)
    {
        var principal = UniquePrincipal();
        var response = operation == "permissions"
            ? "{\"principalId\":\"fixture-principal\",\"permissions\":[\"orders.records.read\",\"orders.records.write\"],\"roles\":[\"reader\"],\"cacheUntil\":\"2026-10-03T00:00:00Z\",\"fromCache\":false}"
            : "{\"principalId\":\"fixture-principal\",\"permissionId\":\"orders.records.read\",\"resourceType\":\"orders\",\"resourceIds\":[\"record-one\",\"record-two\"]}";
        using var rig = new Rig((_, _) => Task.FromResult(JsonResponse(response)));
        if (operation == "permissions")
        {
            Assert.Equal(new[] { "orders.records.read", "orders.records.write" },
                (await rig.Client.GetUserPermissionsAsync(principal)).ToArray());
            var request = AssertRequest(rig, "POST", "/iam/v1/auth/resolve-permissions");
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(principal, body.RootElement.GetProperty("principalId").GetString());
        }
        else
        {
            Assert.Equal(new[] { "record-one", "record-two" },
                (await rig.Client.GetAuthorizedResourcesAsync(principal, "orders.records.read", "orders")).ToArray());
            AssertRequest(rig, "GET",
                $"/iam/v1/auth/authorized-resources?principalId={principal}&permissionId=orders.records.read&resourceType=orders");
        }
        Assert.Empty(rig.Records.Entries);
    }

    [Fact]
    public async Task LiveCallerCancellation_PropagatesWithoutInventingAnIamFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("null");
        });
        using var cancellation = new CancellationTokenSource();
        var principal = UniquePrincipal();
        var pending = rig.Client.CheckPermissionLiveAsync(principal, Permission, Resource, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            AssertPermissionRequest(AssertRequest(rig, "POST", "/iam/v1/auth/check-permission"), principal, Permission, true);
            Assert.Empty(rig.Records.Entries);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task BulkCallerCancellation_ReturnsExplicitDenialsAndPrivateBulkErrorThenDrainsSharedFetches()
    {
        int arrivals = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(async (_, _) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) started.TrySetResult();
            await release.Task;
            return JsonResponse("{\"principalId\":\"fixture-principal\",\"permissionId\":\"fixture-permission\",\"allowed\":false,\"resourcePath\":null,\"fromCache\":false,\"latencyMs\":1}");
        });
        using var cancellation = new CancellationTokenSource();
        var principal = UniquePrincipal();
        PermissionCheckRequest[] requests =
        [
            new() { PermissionId = "orders.records.read", ResourcePath = Resource },
            new() { PermissionId = "orders.records.write", ResourcePath = Resource }
        ];
        try
        {
            var pending = rig.Client.CheckPermissionsAsync(principal, requests, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await pending;
            Assert.Equal(new[] { "orders.records.read", "orders.records.write" }, result.Keys.Order().ToArray());
            Assert.All(result.Values, Assert.False);
            var diagnostic = Assert.Single(rig.Records.Entries);
            Assert.Equal("IamOperationFailure", diagnostic.EventId.Name);
            AssertSafeIam(diagnostic, "ERROR", "Cancelled", principal);
            Assert.Equal(new[] { "IAMService", "IAMService" }, rig.Factory.Names.ToArray());
            Assert.Equal(2, rig.Transport.Requests.Count);
            foreach (var permission in new[] { "orders.records.read", "orders.records.write" })
            {
                var request = Assert.Single(rig.Transport.Requests, candidate =>
                {
                    using var body = JsonDocument.Parse(candidate.Body!);
                    return body.RootElement.GetProperty("permissionId").GetString() == permission;
                });
                Assert.Equal("POST", request.Method);
                Assert.Equal("/iam/v1/auth/check-permission", request.PathAndQuery);
                AssertPermissionRequest(request, principal, permission, false);
            }
        }
        finally
        {
            release.TrySetResult();
            // Normal shared checks do not cancel their upstream fetch. Await the same public-key fetches
            // before disposing transport, rather than leaving background tasks or resetting static caches.
            await Task.WhenAll(requests.Select(request => rig.Client.CheckPermissionAsync(
                principal, request.PermissionId, request.ResourcePath)));
        }
    }

    [Fact]
    public async Task StandardAvailabilityFailure_IsNotCachedAndSubsequentGrantUsesNormalCache()
    {
        int calls = 0;
        using var rig = new Rig((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : JsonResponse("{\"principalId\":\"fixture-principal\",\"permissionId\":\"fixture-permission\",\"allowed\":true,\"resourcePath\":null,\"fromCache\":false,\"latencyMs\":1}")));
        var principal = UniquePrincipal();
        Assert.False(await rig.Client.CheckPermissionAsync(principal, Permission, Resource));
        Assert.True(await rig.Client.CheckPermissionAsync(principal, Permission, Resource));
        Assert.True(await rig.Client.CheckPermissionAsync(principal, Permission, Resource));
        Assert.Equal(2, rig.Transport.Requests.Count);
        Assert.Equal(new[] { "IAMService", "IAMService" }, rig.Factory.Names.ToArray());
        Assert.All(rig.Transport.Requests, request => AssertPermissionRequest(request, principal, Permission, false));
        AssertPrivate(Assert.Single(rig.Records.Entries), "WARNING", null, principal);
    }

    [Fact]
    public async Task BackgroundInvalidRegistration_RecordsPartialFailureWithPrivateJsonAndNoBusResolution()
    {
        using var records = new PrivateProvider();
        using var logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(records));
        using var configuration = new ConfigurationManager();
        using var services = new ServiceCollection().BuildServiceProvider();
        var observedServices = new BusResolutionObserver(services);
        using var lifetime = new StartedLifetime();
        var tracker = new IAMRegistrationStatusTracker();
        var registration = new InvalidRegistration(configuration, logging.CreateLogger<InvalidRegistration>());
        using var service = new BackgroundIAMRegistrationService([registration], observedServices,
            logging.CreateLogger<BackgroundIAMRegistrationService>(), tracker, lifetime);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RegistrationStatus.PartiallyRegistered, tracker.Status);
            Assert.False(tracker.IsRegistered);
            Assert.IsType<AggregateException>(tracker.LastException);
            Assert.Equal(0, observedServices.BusResolutions);
            AssertPrivate(Assert.Single(records.Entries), "ERROR", "System.InvalidOperationException", "private-service-sentinel");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static string UniquePrincipal() => $"private-principal-{Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> FaultResponse(string failure) => failure switch
    {
        "503" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        "malformed" => Task.FromResult(JsonResponse("{private-malformed-response-sentinel")),
        "timeout" => Task.FromException<HttpResponseMessage>(new TimeoutException("private-timeout-sentinel")),
        "unexpected" => Task.FromException<HttpResponseMessage>(new InvalidOperationException("private-unexpected-sentinel")),
        "throw" => Task.FromException<HttpResponseMessage>(new HttpRequestException("private-exception-sentinel")),
        "transport-cancel" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("private-cancellation-sentinel")),
        _ => throw new ArgumentException("Unknown test arrangement.", nameof(failure))
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static RequestSnapshot AssertRequest(Rig rig, string method, string path)
    {
        Assert.Equal("IAMService", Assert.Single(rig.Factory.Names));
        var request = Assert.Single(rig.Transport.Requests);
        Assert.Equal(method, request.Method);
        Assert.Equal(path, request.PathAndQuery);
        return request;
    }

    private static void AssertPermissionRequest(RequestSnapshot request, string principal, string permission, bool live)
    {
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(new[] { "bypassCache", "permissionId", "principalId", "resourcePath" },
            body.RootElement.EnumerateObject().Select(field => field.Name).Order().ToArray());
        Assert.Equal(principal, body.RootElement.GetProperty("principalId").GetString());
        Assert.Equal(permission, body.RootElement.GetProperty("permissionId").GetString());
        Assert.Equal(Resource, body.RootElement.GetProperty("resourcePath").GetString());
        Assert.Equal(live, body.RootElement.GetProperty("bypassCache").GetBoolean());
        Assert.Equal(live, request.Headers.ContainsKey(Header));
        if (live) Assert.Equal(Credential, Assert.Single(request.Headers[Header]));
    }

    private static void AssertSafeIam(PrivateEntry entry, string severity, string failureKind, string principal)
    {
        AssertPrivate(entry, severity, null, principal);
        Assert.Null(entry.Exception);
        Assert.Equal(failureKind, entry.State["FailureKind"]);
        Assert.Equal(5102, entry.EventId.Id);
        Assert.DoesNotContain("private-", entry.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(principal, entry.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, entry.Rendered, StringComparison.Ordinal);
        Assert.All(entry.State.Keys, key => Assert.Contains(key, new[] { "Operation", "FailureKind", "StatusCode", "{OriginalFormat}" }));
    }

    private static void AssertPrivate(PrivateEntry entry, string severity, string? exceptionType, string principal)
    {
        Assert.False(string.IsNullOrEmpty(entry.Json));
        using var json = JsonDocument.Parse(entry.Json);
        Assert.Equal(severity, json.RootElement.GetProperty("severity").GetString());
        Assert.Equal(exceptionType, json.RootElement.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("private-", entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(principal, entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, entry.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("iam-fixture.test", entry.Json, StringComparison.Ordinal);
        foreach (var key in new[] { "PrincipalId", "UserId", "ResourcePath", "Permission", "Credential", "Authorization" })
            Assert.False(json.RootElement.TryGetProperty(key, out _));
    }

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ILoggerFactory _logging;
        private readonly ConfigurationManager _configuration = new();
        private readonly IDisposable? _scope;
        public Rig(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            Transport = new Transport(send);
            _http = new HttpClient(Transport) { BaseAddress = new Uri("https://iam-fixture.test") };
            Factory = new NamedFactory(_http);
            Records = new PrivateProvider();
            _logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(Records));
            var logger = _logging.CreateLogger<IamServiceClient>();
            _scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["Credential"] = Credential,
                ["Authorization"] = "private-authorization-sentinel"
            });
            _configuration["IAM:LivePermissionChecks:Credential"] = Credential;
            Client = new IamServiceClient(Factory, logger, new ProductionEnvironment(), _configuration);
        }
        public IamServiceClient Client { get; }
        public Transport Transport { get; }
        public NamedFactory Factory { get; }
        public PrivateProvider Records { get; }
        public void Dispose()
        {
            try { _scope?.Dispose(); }
            finally
            {
                try { _logging.Dispose(); }
                finally { _http.Dispose(); ((IDisposable)_configuration).Dispose(); }
            }
        }
    }

    private sealed record RequestSnapshot(string Method, string PathAndQuery, string? Body,
        Dictionary<string, string[]> Headers);

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<RequestSnapshot> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // Record production's actual request; assertions run outside its catch-all exception boundary.
            Requests.Enqueue(new RequestSnapshot(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            return await send(request, token);
        }
    }

    private sealed class NamedFactory(HttpClient client) : IHttpClientFactory
    {
        public ConcurrentQueue<string> Names { get; } = new();
        public HttpClient CreateClient(string name) { Names.Enqueue(name); return client; }
    }

    private sealed record PrivateEntry(EventId EventId, string Json, Exception? Exception, string Rendered, IReadOnlyDictionary<string, object?> State);

    private sealed class PrivateProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<PrivateEntry> Entries { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider scopes) => _scopes = scopes;
        public ILogger CreateLogger(string category) => new PrivateLogger(this, category);
        public void Dispose() { }
        private sealed class PrivateLogger(PrivateProvider owner, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
                new PrivateFailureConsoleFormatter().Write(in entry, owner._scopes, writer);
                owner.Entries.Enqueue(new PrivateEntry(eventId, writer.ToString(), exception, formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(value => value.Key, value => value.Value) : new Dictionary<string, object?>()));
            }
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "IamPrivateDiagnosticFixture";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InvalidRegistration(IConfiguration configuration, ILogger logger)
        : IAMRegistrationService(configuration, logger, "private-service-sentinel")
    {
        protected override IEnumerable<PermissionRegistration> GetPermissions() =>
            [new() { PermissionId = "private-invalid-permission-sentinel", Description = "fixture only" }];
        protected override IEnumerable<RoleRegistration> GetPredefinedRoles() => [];
    }

    private sealed class BusResolutionObserver(IServiceProvider services) : IServiceProvider
    {
        private int _busResolutions;
        public int BusResolutions => Volatile.Read(ref _busResolutions);
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IBus)) Interlocked.Increment(ref _busResolutions);
            return services.GetService(serviceType);
        }
    }

    private sealed class StartedLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        public StartedLifetime() => _started.Cancel();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void Dispose() => _started.Dispose();
    }
}
