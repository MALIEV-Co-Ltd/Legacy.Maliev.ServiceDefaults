using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Exercises permission caches across independently configured normal DI hosts.</summary>
public sealed class IamPermissionCacheIsolationTests
{
    private const string Permission = "legacy-auth.invoice-delegation.issue";

    /// <summary>An allow from another host cannot replace this host's authoritative denial.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentHosts_CachedResultCannotReplaceOwnDecision(bool sameOrigin, bool firstAllowed)
    {
        var principal = $"service:cache-isolation-{Guid.NewGuid():N}";
        using var allowed = new Host("https://iam-a.test", allowed: firstAllowed);
        using var denied = new Host(sameOrigin ? "https://iam-a.test" : "https://iam-b.test", allowed: !firstAllowed);
        using var first = allowed.Services.CreateScope();
        using var second = denied.Services.CreateScope();

        Assert.Equal(firstAllowed, await Client(first).CheckPermissionAsync(principal, Permission, "global"));
        Assert.Equal(!firstAllowed, await Client(second).CheckPermissionAsync(principal, Permission, "global"));
        Assert.Equal(1, allowed.Handler.RequestCount);
        Assert.Equal(1, denied.Handler.RequestCount);
    }

    /// <summary>Another host's in-flight grant cannot suppress this host's denial request.</summary>
    [Fact]
    public async Task IndependentHosts_InFlightAllowCannotReplaceDenial()
    {
        var principal = $"service:inflight-isolation-{Guid.NewGuid():N}";
        using var allowed = new Host("https://iam-a.test", allowed: true, blocked: true);
        using var denied = new Host("https://iam-b.test", allowed: false);
        using var first = allowed.Services.CreateScope();
        using var second = denied.Services.CreateScope();
        var pendingAllow = Client(first).CheckPermissionAsync(principal, Permission, "global");
        await allowed.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var independentDenial = Client(second).CheckPermissionAsync(principal, Permission, "global");
        try
        {
            Assert.Equal(1, denied.Handler.RequestCount);
            Assert.False(await independentDenial.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            allowed.Handler.Release.TrySetResult();
            Assert.True(await pendingAllow.WaitAsync(TimeSpan.FromSeconds(5)));
            await independentDenial.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Scoped clients in the same host retain standard cache reuse.</summary>
    [Fact]
    public async Task SameHost_IndependentScopesReuseCachedResult()
    {
        var principal = $"service:scoped-cache-{Guid.NewGuid():N}";
        using var host = new Host("https://iam.test", allowed: true);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        Assert.NotSame(Client(first), Client(second));
        Assert.True(await Client(first).CheckPermissionAsync(principal, Permission, "global"));
        Assert.True(await Client(second).CheckPermissionAsync(principal, Permission, "global"));
        Assert.Equal(1, host.Handler.RequestCount);
    }

    /// <summary>Scoped clients in the same host retain concurrent request coalescing.</summary>
    [Fact]
    public async Task SameHost_IndependentScopesShareInFlightRequest()
    {
        var principal = $"service:scoped-inflight-{Guid.NewGuid():N}";
        using var host = new Host("https://iam.test", allowed: true, blocked: true);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        var initial = Client(first).CheckPermissionAsync(principal, Permission, "global");
        await host.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = Client(second).CheckPermissionAsync(principal, Permission, "global");
        host.Handler.Release.TrySetResult();
        Assert.True(await initial.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await concurrent.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, host.Handler.RequestCount);
    }

    /// <summary>Canceling one scoped waiter cannot cancel another host-local shared waiter.</summary>
    [Fact]
    public async Task SameHost_CanceledScopedWaiterDoesNotCancelSharedFetch()
    {
        var principal = $"service:scoped-cancel-{Guid.NewGuid():N}";
        using var host = new Host("https://iam.test", allowed: true, blocked: true);
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var canceled = Client(first).CheckPermissionAsync(principal, Permission, "global", cancellation.Token);
        await host.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var surviving = Client(second).CheckPermissionAsync(principal, Permission, "global");
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(1, host.Handler.RequestCount);
        }
        finally
        {
            host.Handler.Release.TrySetResult();
            Assert.True(await surviving.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    /// <summary>Live checks bypass and refresh only their own host's permission bucket.</summary>
    [Fact]
    public async Task IndependentHosts_LiveRefreshDoesNotOverwriteOtherHost()
    {
        var principal = $"service:live-isolation-{Guid.NewGuid():N}";
        using var allowed = new Host("https://iam.test", allowed: true, liveCredential: true);
        using var denied = new Host("https://iam.test", allowed: false, liveCredential: true);
        using var first = allowed.Services.CreateScope();
        using var second = denied.Services.CreateScope();
        Assert.True(await Client(first).CheckPermissionAsync(principal, Permission, "global"));
        Assert.False(await Client(second).CheckPermissionAsync(principal, Permission, "global"));
        allowed.Handler.Allowed = false;
        Assert.False(await Client(first).CheckPermissionLiveAsync(principal, Permission, "global"));
        denied.Handler.Allowed = true;
        Assert.True(await Client(second).CheckPermissionLiveAsync(principal, Permission, "global"));
        Assert.False(await Client(first).CheckPermissionAsync(principal, Permission, "global"));
        Assert.True(await Client(second).CheckPermissionAsync(principal, Permission, "global"));
        Assert.Equal([false, true], allowed.Handler.BypassCacheValues);
        Assert.Equal([false, true], denied.Handler.BypassCacheValues);
        Assert.Equal(2, allowed.Handler.RequestCount);
        Assert.Equal(2, denied.Handler.RequestCount);
    }

    private static IIamServiceClient Client(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IIamServiceClient>();

    private sealed class Host : IDisposable
    {
        internal Host(string origin, bool allowed, bool blocked = false, bool liveCredential = false)
        {
            Handler = new PermissionHandler(allowed, blocked);
            var services = new ServiceCollection();
            services.AddLogging();
            var values = new Dictionary<string, string?>();
            if (liveCredential) values["IAM:LivePermissionChecks:Credential"] = Guid.NewGuid().ToString("N");
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            services.AddSingleton<IHostEnvironment>(new TestEnvironment());
            services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri(origin))
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
            services.AddScoped<IIamServiceClient, IamServiceClient>();
            Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        internal ServiceProvider Services { get; }
        internal PermissionHandler Handler { get; }
        public void Dispose() => Services.Dispose();
    }

    private sealed class PermissionHandler(bool allowed, bool blocked) : HttpMessageHandler
    {
        private int requests;
        internal bool Allowed { get; set; } = allowed;
        internal List<bool> BypassCacheValues { get; } = [];
        internal int RequestCount => Volatile.Read(ref requests);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            Started.TrySetResult();
            if (blocked) await Release.Task.WaitAsync(cancellationToken);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri?.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var bypass = body.RootElement.GetProperty("bypassCache").GetBoolean();
            BypassCacheValues.Add(bypass);
            Assert.Equal(bypass, request.Headers.Contains("X-Maliev-IAM-Live-Check-Key"));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { allowed = Allowed, fromCache = false }),
            };
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "cache-isolation-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
