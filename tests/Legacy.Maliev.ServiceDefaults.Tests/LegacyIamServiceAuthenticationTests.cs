using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Checks explicit IAM profile selection without changing existing legacy exchanges.</summary>
public sealed class LegacyIamServiceAuthenticationTests
{
    private static IOptions<LegacyServiceAuthenticationOptions> Credentials() => Options.Create(new LegacyServiceAuthenticationOptions
    {
        ClientId = "legacy-quotation", ClientSecret = "isolated-profile-exchange-secret-0123456789",
    });

    /// <summary>Verifies static routes, credential-only bodies, distinct caches, and profile-specific invalidation.</summary>
    [Fact]
    public async Task IamProfileExchange_LegacyAndIamCachesAndInvalidationRemainSeparate()
    {
        using var factory = new ExchangeFactory();
        var legacy = new LegacyServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        var iam = new LegacyIamServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var legacyFirst = await legacy.GetAccessTokenAsync(deadline.Token);
        var iamFirst = await iam.GetAccessTokenAsync(deadline.Token);
        Assert.NotNull(legacyFirst);
        Assert.NotNull(iamFirst);
        Assert.NotEqual(legacyFirst, iamFirst);
        Assert.Equal(legacyFirst, await legacy.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(iamFirst, await iam.GetAccessTokenAsync(deadline.Token));
        legacy.Invalidate(iamFirst!);
        iam.Invalidate(legacyFirst!);
        Assert.Equal(legacyFirst, await legacy.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(iamFirst, await iam.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(new[] { "/auth/v1/service/login", "/auth/v1/service/iam-login" }, factory.Paths);
        legacy.Invalidate(legacyFirst!);
        Assert.NotEqual(legacyFirst, await legacy.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(iamFirst, await iam.GetAccessTokenAsync(deadline.Token));
        iam.Invalidate(iamFirst!);
        Assert.NotEqual(iamFirst, await iam.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(new[] { "/auth/v1/service/login", "/auth/v1/service/iam-login", "/auth/v1/service/login", "/auth/v1/service/iam-login" }, factory.Paths);
    }

    /// <summary>Denial or unavailability retries only the selected profile and never invokes a legacy fallback.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task IamProfileExchange_DenialNeverFallsBackToLegacy(HttpStatusCode failure)
    {
        using var factory = new ExchangeFactory(failure);
        var iam = new LegacyIamServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Null(await iam.GetAccessTokenAsync(deadline.Token));
        Assert.Null(await iam.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(new[] { "/auth/v1/service/iam-login", "/auth/v1/service/iam-login" }, factory.Paths);
    }

    /// <summary>Missing profile authority stops before transport instead of forwarding an incoming actor token.</summary>
    [Fact]
    public async Task IamProfileHandler_MissingProfileStopsBeforeSendingIncomingActor()
    {
        var provider = new TokenProvider(null);
        var sink = new Sink(HttpStatusCode.OK);
        using var client = new HttpClient(new LegacyIamServiceAuthenticationHandler(provider) { InnerHandler = sink });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://iam-profile.invalid/iam/v1/auth/check-permission");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "incoming-actor-token-not-forwarded");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, deadline.Token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(0, sink.Requests);
        Assert.Null(provider.Invalidated);
    }

    /// <summary>Only the selected IAM profile reaches transport and is invalidated when rejected.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task IamProfileHandler_ReplacesIncomingActorAndInvalidatesRejectedProfile(HttpStatusCode rejected)
    {
        const string token = "isolated-iam-profile-token";
        var provider = new TokenProvider(token);
        var sink = new Sink(rejected);
        using var client = new HttpClient(new LegacyIamServiceAuthenticationHandler(provider) { InnerHandler = sink });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://iam-profile.invalid/iam/v1/auth/check-permission");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "incoming-actor-token-not-forwarded");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(rejected, response.StatusCode);
        Assert.Equal(1, sink.Requests);
        Assert.Equal(token, sink.Token);
        Assert.Equal(token, provider.Invalidated);
    }

    /// <summary>Checks real DI, configured Auth origin, and separation when the legacy provider is already registered.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IamProfileRegistration_UsesConfiguredUnauthenticatedExchangeWithoutReplacingLegacyProvider(bool preRegistered)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ServiceAuthentication:ClientId"] = "legacy-quotation";
        builder.Configuration["ServiceAuthentication:ClientSecret"] = "isolated-profile-exchange-secret-0123456789";
        builder.Configuration["Services:Auth:BaseUrl"] = "https://auth-profile.invalid";
        builder.Configuration["Services:Auth"] = "https://wrong-fallback-origin.invalid";
        var preserved = new LegacyTokenProvider();
        if (preRegistered) builder.Services.AddSingleton<ILegacyServiceAccessTokenProvider>(preserved);
        builder.AddLegacyAuthServiceTokenExchange();
        var exchange = new ExchangeHandler(HttpStatusCode.OK);
        builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => exchange);
        var sink = new Sink(HttpStatusCode.OK);
        builder.Services.AddHttpClient("DedicatedIam", client => client.BaseAddress = new Uri("https://iam-profile.invalid"))
            .ConfigurePrimaryHttpMessageHandler(() => sink).AddLegacyIamServiceAuthentication();
        using var host = builder.Build();
        var services = host.Services;
        if (preRegistered) Assert.Same(preserved, services.GetRequiredService<ILegacyServiceAccessTokenProvider>());
        Assert.NotSame(services.GetRequiredService<ILegacyServiceAccessTokenProvider>(), services.GetRequiredService<ILegacyIamServiceAccessTokenProvider>());
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("DedicatedIam");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/iam/v1/auth/check-permission");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "incoming-actor-token-not-forwarded");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "/auth/v1/service/iam-login" }, exchange.Paths);
        Assert.Equal("isolated-iam-profile-1", sink.Token);
        Assert.Equal(1, sink.Requests);
    }

    /// <summary>Real missing credentials stop both exchange and downstream transport.</summary>
    [Fact]
    public async Task IamProfileHandler_MissingCredentialsStopsBeforeEitherTransport()
    {
        using var factory = new ExchangeFactory();
        var provider = new LegacyIamServiceAccessTokenProvider(factory, Options.Create(new LegacyServiceAuthenticationOptions()), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        var sink = new Sink(HttpStatusCode.OK);
        using var client = new HttpClient(new LegacyIamServiceAuthenticationHandler(provider) { InnerHandler = sink });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://iam-profile.invalid/check");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "incoming-actor-token-not-forwarded");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, deadline.Token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Empty(factory.Paths);
        Assert.Equal(0, sink.Requests);
    }

    /// <summary>Caller cancellation is honored before starting a profile exchange.</summary>
    [Fact]
    public async Task IamProfileExchange_PreCanceledCallerStartsNoExchange()
    {
        using var factory = new ExchangeFactory();
        var provider = new LegacyIamServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAccessTokenAsync(canceled.Token).AsTask());
        Assert.Empty(factory.Paths);
    }

    /// <summary>Concurrent cross-profile invalidations cannot evict either separately cached token.</summary>
    [Fact]
    public async Task IamProfileExchange_ConcurrentCrossInvalidationsPreserveBothCaches()
    {
        using var factory = new ExchangeFactory();
        var legacy = new LegacyServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        var iam = new LegacyIamServiceAccessTokenProvider(factory, Credentials(), TimeProvider.System, NullLogger<LegacyServiceAccessTokenProvider>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var oldToken = await legacy.GetAccessTokenAsync(deadline.Token);
        var iamToken = await iam.GetAccessTokenAsync(deadline.Token);
        Assert.NotNull(oldToken);
        Assert.NotNull(iamToken);
        await Task.WhenAll(Task.Run(() => legacy.Invalidate(iamToken), deadline.Token), Task.Run(() => iam.Invalidate(oldToken), deadline.Token));
        Assert.Equal(oldToken, await legacy.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(iamToken, await iam.GetAccessTokenAsync(deadline.Token));
        Assert.Equal(2, factory.Paths.Length);
    }

    private sealed class LegacyTokenProvider : ILegacyServiceAccessTokenProvider
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("preserved-legacy-token");
        public void Invalidate(string token) { }
    }

    private sealed class TokenProvider(string? token) : ILegacyIamServiceAccessTokenProvider
    {
        public string? Invalidated { get; private set; }
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
        public void Invalidate(string rejected) => Invalidated = rejected;
    }
    private sealed class Sink(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Token { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Token = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
    private sealed class ExchangeFactory : IHttpClientFactory, IDisposable
    {
        private readonly ExchangeHandler handler;
        private readonly HttpClient client;
        public ExchangeFactory(HttpStatusCode status = HttpStatusCode.OK)
        {
            handler = new ExchangeHandler(status);
            client = new HttpClient(handler) { BaseAddress = new Uri("https://auth-profile.invalid"), Timeout = TimeSpan.FromSeconds(30) };
        }
        public string[] Paths => handler.Paths.ToArray();
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(LegacyServiceAccessTokenProvider.HttpClientName, name);
            return client;
        }
        public void Dispose() => client.Dispose();
    }
    private sealed class ExchangeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("auth-profile.invalid", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri!.AbsolutePath;
            Assert.Contains(path, new[] { "/auth/v1/service/login", "/auth/v1/service/iam-login" });
            Paths.Add(path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "clientId", "clientSecret" }, body.RootElement.EnumerateObject().Select(value => value.Name).Order().ToArray());
            Assert.Equal("legacy-quotation", body.RootElement.GetProperty("clientId").GetString());
            var token = (path.EndsWith("iam-login", StringComparison.Ordinal) ? "isolated-iam-profile-" : "isolated-legacy-profile-") + Paths.Count;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { accessToken = token, tokenType = "Bearer", expiresIn = 900 }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
