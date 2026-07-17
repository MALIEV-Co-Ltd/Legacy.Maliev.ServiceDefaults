using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>
/// Regression tests for shared IAM service-client behavior.
/// </summary>
public sealed class IamServiceClientTests
{
    private const string LiveCheckHeaderName = "X-Maliev-IAM-Live-Check-Key";

    /// <summary>
    /// High-volume authorization success-path logs should stay below Information level.
    /// </summary>
    [Fact]
    public void PermissionAuthorizationHandler_UsesDebugForPerRequestPermissionLogs()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "Authorization",
            "PermissionAuthorizationHandler.cs"));

        Assert.Contains("_logger.LogDebug(\"Checking permission {Permission}", source, StringComparison.Ordinal);
        Assert.Contains("_logger.LogDebug(\"JWT contains wildcard permission", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger.LogInformation(\"Checking permission {Permission}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger.LogInformation(\"JWT contains wildcard permission", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Concurrent identical permission checks should share one upstream IAM request.
    /// </summary>
    [Fact]
    public async Task CheckPermissionAsync_ConcurrentIdenticalChecks_UsesSingleUpstreamRequest()
    {
        var handler = new CountingPermissionHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://iam.test")
        };

        var client = new IamServiceClient(
            new StaticHttpClientFactory(httpClient),
            NullLogger<IamServiceClient>.Instance,
            new TestHostEnvironment(),
            CreateConfiguration());

        var principalId = $"system:service:test-{Guid.NewGuid():N}";
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => client.CheckPermissionAsync(principalId, "material.materials.read", "global"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// Existing callers of the original constructor retain cached checks while live checks fail closed.
    /// </summary>
    [Fact]
    public async Task LegacyConstructor_StandardAndLiveChecks_PreservesCompatibilityAndFailsLiveClosed()
    {
        var handler = new CountingPermissionHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://iam.test")
        };
        var client = new IamServiceClient(
            new StaticHttpClientFactory(httpClient),
            NullLogger<IamServiceClient>.Instance,
            new TestHostEnvironment());
        var principalId = $"legacy-user-{Guid.NewGuid():N}";

        var standardResult = await client.CheckPermissionAsync(
            principalId,
            "project.projects.read",
            "projects/project-123");
        var liveResult = await client.CheckPermissionLiveAsync(
            principalId,
            "project.projects.read",
            "projects/project-123");

        Assert.True(standardResult);
        Assert.False(liveResult);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// Forced-live checks must bypass a previously cached standard result and request an authoritative IAM read.
    /// </summary>
    [Fact]
    public async Task CheckPermissionLiveAsync_CachedStandardResult_SendsBypassRequest()
    {
        const string liveCheckCredential = "test-live-check-credential";
        var handler = new LivePermissionHandler(liveCheckCredential);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://iam.test")
        };
        var client = new IamServiceClient(
            new StaticHttpClientFactory(httpClient),
            NullLogger<IamServiceClient>.Instance,
            new TestHostEnvironment(),
            CreateConfiguration(liveCheckCredential));
        var principalId = $"user-{Guid.NewGuid():N}";

        var standardResult = await client.CheckPermissionAsync(
            principalId,
            "project.projects.read",
            "projects/project-123");
        var liveResult = await client.CheckPermissionLiveAsync(
            principalId,
            "project.projects.read",
            "projects/project-123");

        Assert.True(standardResult);
        Assert.False(liveResult);
        Assert.Equal([false, true], handler.BypassCacheValues);
        Assert.Equal([false, true], handler.LiveCheckCredentialMatches);
    }

    /// <summary>
    /// Caller-triggered cancellation of an authoritative live check must propagate to the caller.
    /// </summary>
    [Fact]
    public async Task CheckPermissionLiveAsync_CallerCancels_PropagatesCancellation()
    {
        const string liveCheckCredential = "test-live-check-credential";
        var handler = new CancellablePermissionHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://iam.test")
        };
        var client = new IamServiceClient(
            new StaticHttpClientFactory(httpClient),
            NullLogger<IamServiceClient>.Instance,
            new TestHostEnvironment(),
            CreateConfiguration(liveCheckCredential));
        using var cancellationSource = new CancellationTokenSource();

        var checkTask = client.CheckPermissionLiveAsync(
            $"user-{Guid.NewGuid():N}",
            "project.projects.read",
            "projects/project-123",
            cancellationSource.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkTask);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// A forced-live check without its dedicated credential must fail closed before creating an IAM request.
    /// </summary>
    [Fact]
    public async Task CheckPermissionLiveAsync_MissingCredential_FailsClosedWithoutHttpRequest()
    {
        var handler = new CountingPermissionHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://iam.test")
        };
        var client = new IamServiceClient(
            new StaticHttpClientFactory(httpClient),
            NullLogger<IamServiceClient>.Instance,
            new TestHostEnvironment(),
            CreateConfiguration());

        var result = await client.CheckPermissionLiveAsync(
            $"user-{Guid.NewGuid():N}",
            "project.projects.read",
            "projects/project-123");

        Assert.False(result);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// The live-check credential header must be configured as sensitive in HttpClient logs.
    /// </summary>
    [Fact]
    public void AddIamClient_RedactsLiveCheckCredentialHeader()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "Extensions.IAM.cs"));
        var clientSource = File.ReadAllText(FindRepoFile(
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "IAM",
            "IamServiceClient.cs"));

        Assert.Contains($"RedactLoggedHeaders([\"{LiveCheckHeaderName}\"])", source, StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.Exchange(ref _missingLiveCheckCredentialLogged, 1) == 0",
            clientSource,
            StringComparison.Ordinal);
    }

    private sealed class CountingPermissionHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(100, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    PrincipalId = "system:service:test",
                    PermissionId = "material.materials.read",
                    Allowed = true,
                    ResourcePath = "global",
                    FromCache = false,
                    LatencyMs = 1
                })
            };
        }
    }

    private sealed class LivePermissionHandler(string expectedLiveCheckCredential) : HttpMessageHandler
    {
        public List<bool> BypassCacheValues { get; } = [];

        public List<bool> LiveCheckCredentialMatches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var bypassCache = payload.TryGetProperty("bypassCache", out var value) && value.GetBoolean();
            BypassCacheValues.Add(bypassCache);
            var credentialMatches = request.Headers.TryGetValues(LiveCheckHeaderName, out var values) &&
                string.Equals(values.Single(), expectedLiveCheckCredential, StringComparison.Ordinal);
            LiveCheckCredentialMatches.Add(credentialMatches);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    PrincipalId = "user-123",
                    PermissionId = "project.projects.read",
                    Allowed = !bypassCache,
                    ResourcePath = "projects/project-123",
                    FromCache = false,
                    LatencyMs = 1
                })
            };
        }
    }

    private sealed class CancellablePermissionHandler : HttpMessageHandler
    {
        private int _requestCount;

        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static IConfiguration CreateConfiguration(string? liveCheckCredential = null)
    {
        var values = new Dictionary<string, string?>();
        if (liveCheckCredential is not null)
        {
            values["IAM:LivePermissionChecks:Credential"] = liveCheckCredential;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StaticHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name) => _httpClient;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Legacy.Maliev.ServiceDefaults.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not find repository file.", Path.Combine(relativeParts));
    }
}
