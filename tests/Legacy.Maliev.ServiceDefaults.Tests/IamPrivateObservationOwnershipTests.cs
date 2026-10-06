using System.Collections.Concurrent;
using System.Net;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class IamPrivateObservationOwnershipTests
{
    [Theory]
    [InlineData("resolve", "503", true)]
    [InlineData("check", "503", true)]
    [InlineData("live", "503", true)]
    [InlineData("resources", "503", true)]
    [InlineData("resolve", "throw", false)]
    [InlineData("check", "throw", false)]
    [InlineData("live", "throw", false)]
    [InlineData("resources", "throw", false)]
    [InlineData("resolve", "body-deadline", false)]
    [InlineData("check", "body-deadline", false)]
    [InlineData("live", "body-deadline", false)]
    [InlineData("resources", "body-deadline", false)]
    [InlineData("resolve", "malformed", false)]
    [InlineData("check", "malformed", false)]
    [InlineData("live", "malformed", false)]
    [InlineData("resources", "malformed", false)]
    [InlineData("resolve", "deadline", false)]
    [InlineData("check", "deadline", false)]
    [InlineData("live", "deadline", false)]
    [InlineData("resources", "deadline", false)]
    public async Task SelectedTransportFailureHasOneSafeOwnerIncludingNativeDeadlineAndThrowingSink(
        string operation, string failure, bool throwingSink)
    {
        var records = new Records(throwingSink);
        var transport = new Transport(failure);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(records)
            .AddFilter((category, _) => category?.EndsWith("IamServiceClient", StringComparison.Ordinal) == true ||
                category?.EndsWith("PrivateDependencyFailureHandler", StringComparison.Ordinal) == true));
        services.AddHttpClient("IAMService", client =>
        {
            client.BaseAddress = new Uri("https://private-iam-host.invalid");
            client.Timeout = TimeSpan.FromMilliseconds(200);
        }).ConfigurePrimaryHttpMessageHandler(() => transport).AddPrivateFailureObservation("IAMService");
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IAM:LivePermissionChecks:Credential"] = "private-live-credential-sentinel",
        }).Build();
        var sut = new IamServiceClient(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<IamServiceClient>>(), new ProductionEnvironment(), configuration);
        const string principal = "private-principal-sentinel";
        const string permission = "private-permission-sentinel";
        const string resource = "private-resource-sentinel";
        switch (operation)
        {
            case "resolve": Assert.Empty(await sut.GetUserPermissionsAsync(principal)); break;
            case "check": Assert.False(await sut.CheckPermissionAsync(principal, permission, resource)); break;
            case "live": Assert.False(await sut.CheckPermissionLiveAsync(principal, permission, resource)); break;
            case "resources": Assert.Empty(await sut.GetAuthorizedResourcesAsync(principal, permission, resource)); break;
            default: throw new InvalidOperationException("Unknown fixture operation");
        }
        Assert.Equal(1, transport.Calls);
        var entry = Assert.Single(records.Entries);
        Assert.Equal(failure is "malformed" or "body-deadline" ? 5102 : 5101, entry.Id);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("private-", entry.Rendered, StringComparison.Ordinal);
        Assert.All(entry.State.Keys, key => Assert.Contains(key,
            new[] { "EventName", "Dependency", "Operation", "FailureKind", "StatusCode", "{OriginalFormat}" }));
        if (failure == "malformed") Assert.Equal("InvalidJson", entry.State["FailureKind"]);
        else if (failure == "body-deadline") Assert.Equal("Cancelled", entry.State["FailureKind"]);
        else Assert.Equal("IAMService", entry.State["Dependency"]);
        if (failure == "503") Assert.Equal(503, entry.State["StatusCode"]);
        if (failure == "deadline") Assert.True(transport.CancellationObserved);
        if (failure == "body-deadline") Assert.True(transport.BodyCancellationObserved);
    }

    [Theory]
    [InlineData("resolve", false)]
    [InlineData("resolve", true)]
    [InlineData("resources", false)]
    [InlineData("resources", true)]
    public async Task LookupRequestPreservesConfiguredNativeVersionAndVersionPolicy(string operation, bool selected)
    {
        var transport = new VersionTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHttpClient("IAMService", client =>
        {
            client.BaseAddress = new Uri("https://private-version-host.invalid");
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }).ConfigurePrimaryHttpMessageHandler(() => transport);
        if (selected) builder.AddPrivateFailureObservation("IAMService");
        await using var provider = services.BuildServiceProvider();
        var sut = new IamServiceClient(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<IamServiceClient>>(), new ProductionEnvironment());
        if (operation == "resolve") Assert.Empty(await sut.GetUserPermissionsAsync("private-version-principal"));
        else Assert.Empty(await sut.GetAuthorizedResourcesAsync("private-version-principal", "private-permission", "private-resource"));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(HttpVersion.Version20, transport.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, transport.Policy);
    }

    private sealed class VersionTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Version? Version { get; private set; }
        public HttpVersionPolicy Policy { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Version = request.Version;
            Policy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Transport(string failure) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CancellationObserved { get; private set; }
        public bool BodyCancellationObserved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (failure == "body-deadline") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new PendingBody(() => BodyCancellationObserved = true),
            };
            if (failure == "503") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (failure == "malformed") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{private-malformed-body-sentinel", System.Text.Encoding.UTF8, "application/json"),
            };
            if (failure == "throw") throw new HttpRequestException("private-transport-exception-sentinel");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            throw new InvalidOperationException("Unreachable fixture transport");
        }
    }

    private sealed class PendingBody(Action cancelled) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled();
                throw;
            }
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed record Entry(int Id, string Rendered, Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class Records(bool throwingSink) : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, throwingSink);
        public void Dispose() { }
        private sealed class Logger(Records owner, bool throwingSink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
                owner.Entries.Enqueue(new Entry(eventId.Id, formatter(state, exception), exception,
                    values.ToDictionary(value => value.Key, value => value.Value)));
                if (throwingSink) throw new InvalidOperationException("private-sink-exception-sentinel");
            }
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "IamObservationFixture";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
