using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>
/// Security and transport tests for the opt-in AuthService service-token exchange.
/// </summary>
public sealed class AuthServiceTokenExchangeTests : IDisposable
{
    private const string ClientId = "service-dev-quote-engine";
    private const string ClientSecret = "a-test-client-secret-with-sufficient-entropy";
    private const string ServiceName = "QuoteEngineBff";
    private const string Issuer = "https://api.maliev.com";
    private const string Audience = "https://api.maliev.com";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-07-16T00:00:00Z"));

    /// <summary>Verifies the client-credential request uses the AuthService snake-case contract.</summary>
    [Fact]
    public async Task GetTokenAsync_ValidExchange_UsesSnakeCaseWireContract()
    {
        string? requestBody = null;
        var transport = new StubHttpMessageHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/auth/v1/service/login", request.RequestUri?.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            requestBody = await request.Content!.ReadAsStringAsync();
            return CreateSuccessResponse(CreateServiceToken());
        });
        var provider = CreateProvider(transport);

        var token = await provider.GetTokenAsync();

        Assert.False(string.IsNullOrWhiteSpace(token));
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal(ClientId, json.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(ClientSecret, json.RootElement.GetProperty("client_secret").GetString());
        Assert.False(json.RootElement.TryGetProperty("clientId", out _));
        Assert.False(json.RootElement.TryGetProperty("clientSecret", out _));
        Assert.Equal(1, transport.RequestCount);
    }

    /// <summary>Verifies tokens cannot substitute either configured identity dimension.</summary>
    [Theory]
    [InlineData("different-client", ServiceName)]
    [InlineData(ClientId, "DifferentService")]
    public async Task GetTokenAsync_TokenIdentityMismatch_FailsClosed(string tokenClientId, string tokenServiceName)
    {
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken(
            clientId: tokenClientId,
            serviceName: tokenServiceName)));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies wildcard authority is never accepted from the central issuer.</summary>
    [Theory]
    [InlineData("permissions")]
    [InlineData("permission")]
    [InlineData("roles")]
    [InlineData("role")]
    public async Task GetTokenAsync_WildcardAuthority_FailsClosed(string claimType)
    {
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken(
            additionalClaims: [new Claim(claimType, "*")])));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies an otherwise well-formed token signed by another key is rejected.</summary>
    [Fact]
    public async Task GetTokenAsync_InvalidSignature_FailsClosed()
    {
        using var untrustedKey = RSA.Create(2048);
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken(signingKey: untrustedKey)));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies symmetric tokens are rejected by the RS256-only exchange path.</summary>
    [Fact]
    public async Task GetTokenAsync_Hs256Token_FailsClosed()
    {
        var now = _time.GetUtcNow();
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, "11111111-1111-1111-1111-111111111111"),
                new Claim("client_id", ClientId),
                new Claim("service_name", ServiceName),
                new Claim("user_type", "service")
            ],
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(15).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-test-signing-key-that-is-at-least-32-bytes")),
                SecurityAlgorithms.HmacSha256));
        var transport = RespondWith(() => CreateSuccessResponse(new JwtSecurityTokenHandler().WriteToken(token)));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies a token whose not-before time is in the future is rejected.</summary>
    [Fact]
    public async Task GetTokenAsync_FutureNotBefore_FailsClosed()
    {
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken(
            notBeforeOffset: TimeSpan.FromMinutes(2))));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
    }

    /// <summary>Verifies a token whose issued-at time is in the future is rejected independently.</summary>
    [Fact]
    public async Task GetTokenAsync_FutureIssuedAt_FailsClosed()
    {
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken(
            issuedAtOffset: TimeSpan.FromMinutes(2))));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
    }

    /// <summary>Verifies a duplicated subject cannot be collapsed into one trusted identity.</summary>
    [Fact]
    public async Task GetTokenAsync_DuplicateSubject_FailsClosed()
    {
        const string principalId = "11111111-1111-1111-1111-111111111111";
        var transport = RespondWith(() => CreateSuccessResponse(
            CreateServiceToken(additionalClaims: [new Claim(JwtRegisteredClaimNames.Sub, principalId)]),
            responsePrincipalId: principalId));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
    }

    /// <summary>Verifies an empty subject is never accepted as a service principal.</summary>
    [Fact]
    public async Task GetTokenAsync_EmptySubject_FailsClosed()
    {
        var transport = RespondWith(() => CreateSuccessResponse(
            CreateServiceToken(subject: string.Empty),
            responsePrincipalId: string.Empty));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
    }

    /// <summary>Verifies an opaque non-Guid subject cannot become a service principal.</summary>
    [Fact]
    public async Task GetTokenAsync_NonGuidSubject_FailsClosed()
    {
        const string principalId = "opaque-service-principal";
        var transport = RespondWith(() => CreateSuccessResponse(
            CreateServiceToken(subject: principalId),
            responsePrincipalId: principalId));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
    }

    /// <summary>Verifies literal PEM public-key configuration remains compatible.</summary>
    [Fact]
    public async Task GetTokenAsync_LiteralPemPublicKey_ValidatesToken()
    {
        var transport = RespondWith(() => CreateSuccessResponse(CreateServiceToken()));
        var provider = CreateProvider(transport, publicKey: _rsa.ExportSubjectPublicKeyInfoPem());

        var token = await provider.GetTokenAsync();

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    /// <summary>Verifies response identity metadata must agree with the validated token.</summary>
    [Fact]
    public async Task GetTokenAsync_ResponseIdentityMismatch_FailsClosed()
    {
        var transport = RespondWith(() => CreateSuccessResponse(
            CreateServiceToken(),
            responseClientId: "different-client"));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies conflicting JWT and response expirations are rejected.</summary>
    [Fact]
    public async Task GetTokenAsync_InconsistentExpiry_FailsClosed()
    {
        var transport = RespondWith(() => CreateSuccessResponse(
            CreateServiceToken(lifetimeSeconds: 900),
            expiresIn: 300));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies network receipt latency does not make a valid server lifetime inconsistent.</summary>
    [Fact]
    public async Task GetTokenAsync_DelayedReceipt_UsesExchangeStartForExpiryConsistency()
    {
        string? token = null;
        var transport = new StubHttpMessageHandler((_, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(8));
            token = CreateServiceToken(lifetimeSeconds: 900);
            return Task.FromResult(CreateSuccessResponse(token, expiresIn: 900));
        });
        var provider = CreateProvider(transport);

        var exchanged = await provider.GetTokenAsync();

        Assert.Equal(token, exchanged);
    }

    /// <summary>Verifies cached tokens refresh before their effective expiry.</summary>
    [Fact]
    public async Task GetTokenAsync_CachesUntilBoundedSafetyMarginThenRefreshes()
    {
        var issued = 0;
        var transport = RespondWith(() =>
        {
            issued++;
            return CreateSuccessResponse(CreateServiceToken(jti: issued.ToString()));
        });
        var provider = CreateProvider(transport, refreshSafetyMarginSeconds: 60);

        var first = await provider.GetTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(839));
        var cached = await provider.GetTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await provider.GetTokenAsync();

        Assert.Equal(first, cached);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(2, transport.RequestCount);
    }

    /// <summary>Verifies concurrent callers share one credential exchange.</summary>
    [Fact]
    public async Task GetTokenAsync_ConcurrentCallers_ShareOneExchange()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return CreateSuccessResponse(CreateServiceToken());
        });
        var provider = CreateProvider(transport);

        var callers = Enumerable.Range(0, 20)
            .Select(_ => provider.GetTokenAsync())
            .ToArray();
        await WaitUntilAsync(() => transport.RequestCount == 1);
        release.SetResult();

        var tokens = await Task.WhenAll(callers);

        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, transport.RequestCount);
    }

    /// <summary>Verifies canceling one waiter does not cancel a shared refresh.</summary>
    [Fact]
    public async Task GetTokenAsync_CanceledWaiter_DoesNotCancelSharedExchange()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return CreateSuccessResponse(CreateServiceToken());
        });
        var provider = CreateProvider(transport);
        using var canceled = new CancellationTokenSource();

        var firstWaiter = provider.GetTokenAsync(canceled.Token);
        var survivingWaiter = provider.GetTokenAsync();
        await WaitUntilAsync(() => transport.RequestCount == 1);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWaiter);
        release.SetResult();

        var token = await survivingWaiter;

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(1, transport.RequestCount);
    }

    /// <summary>Verifies an orphaned failed refresh is observed and cleared without another caller.</summary>
    [Fact]
    public async Task GetTokenAsync_AllWaitersCancelThenRefreshFails_ProactivelyClearsRefresh()
    {
        var attempt = 0;
        var failedResponse = new TaskCompletionSource<HttpResponseMessage>();
        var transport = new StubHttpMessageHandler((_, _) =>
        {
            return Interlocked.Increment(ref attempt) == 1
                ? failedResponse.Task
                : Task.FromResult(CreateSuccessResponse(CreateServiceToken()));
        });
        var provider = CreateProvider(transport);
        using var canceled = new CancellationTokenSource();

        var orphanedWaiter = provider.GetTokenAsync(canceled.Token);
        await WaitUntilAsync(() => transport.RequestCount == 1);
        var refreshTask = Assert.IsAssignableFrom<Task>(GetActiveRefresh(provider));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orphanedWaiter);
        failedResponse.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await WaitUntilAsync(() => GetActiveRefresh(provider) is null);

        Assert.True(refreshTask.IsFaulted);

        var recovered = await provider.GetTokenAsync();

        Assert.False(string.IsNullOrWhiteSpace(recovered));
        Assert.Equal(2, transport.RequestCount);
    }

    /// <summary>Verifies an orphaned failed refresh is not replayed to the next caller.</summary>
    [Fact]
    public async Task GetTokenAsync_AllWaitersCancelThenRefreshFails_NextCallerStartsFreshExchange()
    {
        var attempt = 0;
        var failedResponse = new TaskCompletionSource<HttpResponseMessage>();
        var transport = new StubHttpMessageHandler((_, _) =>
        {
            return Interlocked.Increment(ref attempt) == 1
                ? failedResponse.Task
                : Task.FromResult(CreateSuccessResponse(CreateServiceToken()));
        });
        var provider = CreateProvider(transport);
        using var canceled = new CancellationTokenSource();

        var orphanedWaiter = provider.GetTokenAsync(canceled.Token);
        await WaitUntilAsync(() => transport.RequestCount == 1);
        var refreshTask = Assert.IsAssignableFrom<Task>(GetActiveRefresh(provider));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orphanedWaiter);
        failedResponse.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => refreshTask);

        var recovered = await provider.GetTokenAsync();

        Assert.False(string.IsNullOrWhiteSpace(recovered));
        Assert.Equal(2, transport.RequestCount);
    }

    /// <summary>Verifies a failed refresh is cleared so the next caller can recover.</summary>
    [Fact]
    public async Task GetTokenAsync_FailedRefresh_AllowsNextRefreshAttempt()
    {
        var attempt = 0;
        var transport = RespondWith(() =>
        {
            attempt++;
            return attempt == 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : CreateSuccessResponse(CreateServiceToken(jti: attempt.ToString()));
        });
        var provider = CreateProvider(transport, refreshSafetyMarginSeconds: 60);

        var first = await provider.GetTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(841));
        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() => provider.GetTokenAsync());
        var recovered = await provider.GetTokenAsync();

        Assert.NotEqual(first, recovered);
        Assert.Equal(3, transport.RequestCount);
    }

    /// <summary>Verifies AuthService failure responses never yield a token.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetTokenAsync_NonSuccessResponse_FailsClosed(HttpStatusCode statusCode)
    {
        var transport = RespondWith(() => new HttpResponseMessage(statusCode));
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies failure diagnostics do not disclose credential or upstream content.</summary>
    [Fact]
    public async Task GetTokenAsync_Failure_DoesNotExposeCredentialsTokenOrResponseBody()
    {
        const string sensitiveResponse = "sensitive-upstream-diagnostic";
        var transport = RespondWith(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(sensitiveResponse)
        });
        var provider = CreateProvider(transport);

        var exception = await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());

        Assert.DoesNotContain(ClientSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveResponse, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies malformed success content fails closed.</summary>
    [Fact]
    public async Task GetTokenAsync_MalformedResponse_FailsClosed()
    {
        var transport = RespondWith(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":", Encoding.UTF8, "application/json")
        });
        var provider = CreateProvider(transport);

        await Assert.ThrowsAsync<ServiceTokenExchangeException>(() =>
            provider.GetTokenAsync());
    }

    /// <summary>Verifies the opt-in handler writes the validated bearer token.</summary>
    [Fact]
    public async Task ExchangeHandler_ValidToken_SetsBearerAuthorizationHeader()
    {
        var token = CreateServiceToken();
        var provider = new StubTokenProvider(token);
        HttpRequestMessage? capturedRequest = null;
        var terminal = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var handler = new AuthServiceTokenExchangeHandler(provider)
        {
            InnerHandler = terminal
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://downstream.test/resource"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", token), capturedRequest!.Headers.Authorization);
    }

    /// <summary>Verifies invalid credential bounds fail during host startup.</summary>
    [Fact]
    public async Task AddAuthServiceTokenExchange_InvalidOptions_FailsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = "x",
            ["ServiceAuthentication:ClientSecret"] = "short",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        builder.AddAuthServiceTokenExchange(ServiceName);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync());
    }

    /// <summary>Verifies missing JWT trust metadata fails during host startup.</summary>
    [Fact]
    public async Task AddAuthServiceTokenExchange_MissingJwtValidationMetadata_FailsHostStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = ClientId,
            ["ServiceAuthentication:ClientSecret"] = ClientSecret
        });
        builder.AddAuthServiceTokenExchange(ServiceName);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies a present but malformed public key fails before serving traffic.</summary>
    [Fact]
    public async Task AddAuthServiceTokenExchange_MalformedPublicKey_FailsHostStartup()
    {
        using var host = CreateExchangeHost("not-a-public-key");

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies a non-RSA subject public key fails before serving traffic.</summary>
    [Fact]
    public async Task AddAuthServiceTokenExchange_NonRsaPublicKey_FailsHostStartup()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var host = CreateExchangeHost(ec.ExportSubjectPublicKeyInfoPem());

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies an undersized RSA key fails before serving traffic.</summary>
    [Fact]
    public async Task AddAuthServiceTokenExchange_UndersizedRsaPublicKey_FailsHostStartup()
    {
        using var weakRsa = RSA.Create(1024);
        using var host = CreateExchangeHost(weakRsa.ExportSubjectPublicKeyInfoPem());

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies issuer and audience trust identifiers must be absolute HTTPS URLs.</summary>
    [Theory]
    [InlineData("http://api.maliev.com", Audience)]
    [InlineData(Issuer, "relative-audience")]
    [InlineData("https://api.maliev.com/a/../b", Audience)]
    public async Task AddAuthServiceTokenExchange_InvalidTrustIdentifier_FailsHostStartup(
        string issuer,
        string audience)
    {
        using var host = CreateExchangeHost(_rsa.ExportSubjectPublicKeyInfoPem(), issuer, audience);

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies production never sends service credentials to a plaintext AuthService endpoint.</summary>
    [Theory]
    [InlineData("http://auth.internal")]
    [InlineData("http://localhost")]
    public async Task AddAuthServiceTokenExchange_ProductionHttpBaseUrl_FailsHostStartup(string baseUrl)
    {
        using var host = CreateExchangeHost(
            _rsa.ExportSubjectPublicKeyInfoPem(),
            authServiceBaseUrl: baseUrl,
            environmentName: "Production");

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies local plaintext exchange is limited to loopback in local-only environments.</summary>
    [Theory]
    [InlineData("Development", "http://localhost")]
    [InlineData("Development", "http://127.0.0.1:5100")]
    [InlineData("Testing", "http://[::1]:5100")]
    public async Task AddAuthServiceTokenExchange_LocalEnvironmentLoopbackHttpBaseUrl_Starts(
        string environmentName,
        string baseUrl)
    {
        using var host = CreateExchangeHost(
            _rsa.ExportSubjectPublicKeyInfoPem(),
            authServiceBaseUrl: baseUrl,
            environmentName: environmentName);

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>Verifies a local environment cannot opt into plaintext exchange with a non-loopback peer.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task AddAuthServiceTokenExchange_LocalEnvironmentNonLoopbackHttpBaseUrl_FailsHostStartup(
        string environmentName)
    {
        using var host = CreateExchangeHost(
            _rsa.ExportSubjectPublicKeyInfoPem(),
            authServiceBaseUrl: "http://auth.internal",
            environmentName: environmentName);

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies unsafe or ambiguous AuthService endpoint syntax fails before serving traffic.</summary>
    [Theory]
    [InlineData("https://user@auth.test")]
    [InlineData("https://auth.test?region=one")]
    [InlineData("https://auth.test#fragment")]
    [InlineData("https://auth.test/a/../b")]
    [InlineData(" https://auth.test")]
    [InlineData("https://AUTH.test")]
    [InlineData("https://auth.test/")]
    public async Task AddAuthServiceTokenExchange_NonCanonicalBaseUrl_FailsHostStartup(string baseUrl)
    {
        using var host = CreateExchangeHost(
            _rsa.ExportSubjectPublicKeyInfoPem(),
            authServiceBaseUrl: baseUrl,
            environmentName: "Production");

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    /// <summary>Verifies local Aspire keeps HTTPS-first discovery with an HTTP fallback.</summary>
    [Theory]
    [InlineData("Development", "https+http://authservice/")]
    [InlineData("Testing", "https+http://authservice/")]
    [InlineData("Production", "https://authservice/")]
    public void AddAuthServiceTokenExchange_DefaultDiscoveryTransport_IsEnvironmentSafe(
        string environmentName,
        string expectedBaseUrl)
    {
        using var host = CreateExchangeHost(
            _rsa.ExportSubjectPublicKeyInfoPem(),
            environmentName: environmentName);
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AuthServiceTokenProvider.HttpClientName);

        Assert.Equal(new Uri(expectedBaseUrl), client.BaseAddress);
    }

    /// <summary>Verifies the provider owns disposable cryptographic key material.</summary>
    [Fact]
    public void AuthServiceTokenProvider_OwnsDisposableKeyMaterial()
    {
        var provider = CreateProvider(RespondWith(() => CreateSuccessResponse(CreateServiceToken())));

        var disposable = Assert.IsAssignableFrom<IDisposable>(provider);
        disposable.Dispose();
    }

    /// <summary>Verifies registration adds only the new opt-in provider and exchange client.</summary>
    [Fact]
    public void AddAuthServiceTokenExchange_RegistersOptInProviderAndDedicatedClient()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = ClientId,
            ["ServiceAuthentication:ClientSecret"] = ClientSecret,
            ["Services:AuthService:BaseUrl"] = "https://auth.test",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });

        builder.AddAuthServiceTokenExchange(ServiceName);

        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<AuthServiceTokenProvider>(provider.GetRequiredService<IAuthServiceTokenProvider>());
        Assert.NotNull(provider.GetRequiredService<AuthServiceTokenExchangeHandler>());
        var exchangeClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AuthServiceTokenProvider.HttpClientName);
        Assert.Equal(new Uri("https://auth.test/"), exchangeClient.BaseAddress);
    }

    /// <summary>Verifies the downstream client helper attaches the opt-in handler.</summary>
    [Fact]
    public async Task AddAuthServiceAuthentication_AttachesOptInHandlerToDownstreamClient()
    {
        var token = CreateServiceToken();
        HttpRequestMessage? capturedRequest = null;
        var terminal = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var services = new ServiceCollection();
        services.AddSingleton<IAuthServiceTokenProvider>(new StubTokenProvider(token));
        services.AddTransient<AuthServiceTokenExchangeHandler>();
        services.AddHttpClient("downstream", client => client.BaseAddress = new Uri("https://downstream.test"))
            .ConfigurePrimaryHttpMessageHandler(() => terminal)
            .AddAuthServiceAuthentication();
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("downstream");

        using var response = await client.GetAsync("/resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", token), capturedRequest!.Headers.Authorization);
    }

    /// <summary>Verifies the IAM client uses the central exchange without registering legacy signing.</summary>
    [Fact]
    public void AddAuthServiceIAMClient_RegistersCentralExchangeClientWithoutLegacySigner()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = ClientId,
            ["ServiceAuthentication:ClientSecret"] = ClientSecret,
            ["Services:AuthService:BaseUrl"] = "https://auth.test",
            ["Services:IAMService:BaseUrl"] = "https://iam.test",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        builder.AddAuthServiceTokenExchange(ServiceName);

        builder.AddAuthServiceIAMClient();

        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<IamServiceClient>(provider.GetRequiredService<IIamServiceClient>());
        Assert.Null(provider.GetService<IServiceAccountTokenProvider>());
        Assert.Null(provider.GetService<ServiceAccountAuthenticationHandler>());
        var iamClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        Assert.Equal(new Uri("https://iam.test/"), iamClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(30), iamClient.Timeout);
    }

    /// <summary>Verifies service tokens cannot be sent to a plaintext production IAM origin.</summary>
    [Fact]
    public void AddAuthServiceIAMClient_ProductionHttpBaseUrl_FailsClosed()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration["Services:IAMService:BaseUrl"] = "http://iam.internal";
        builder.AddAuthServiceTokenExchange(ServiceName);

        Assert.Throws<InvalidOperationException>(() => builder.AddAuthServiceIAMClient());
    }

    /// <summary>Verifies the legacy IAM registration cannot mutate a central-exchange client graph.</summary>
    [Fact]
    public void AddAuthServiceIAMClient_ThenLegacyIAMClient_RejectsMixedAuthenticationBeforeMutation()
    {
        var builder = CreateConfiguredExchangeBuilder();
        builder.AddAuthServiceTokenExchange(ServiceName);
        builder.AddAuthServiceIAMClient();
        var descriptorCount = builder.Services.Count;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddIAMServiceClient("legacy-search"));

        Assert.Contains("cannot be combined", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(descriptorCount, builder.Services.Count);
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IServiceAccountTokenProvider));
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(ServiceAccountAuthenticationHandler));
    }

    /// <summary>Verifies an existing central registration wins over missing legacy configuration.</summary>
    [Fact]
    public void AddAuthServiceIAMClient_ThenUnconfiguredLegacyIAMClient_ReportsAuthenticationConflict()
    {
        var builder = CreateConfiguredExchangeBuilder();
        builder.AddAuthServiceTokenExchange(ServiceName);
        builder.AddAuthServiceIAMClient();
        builder.Configuration["ServiceName"] = null;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddIAMServiceClient());

        Assert.Contains("cannot be combined", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies the central IAM registration cannot mutate a legacy-authenticated client graph.</summary>
    [Fact]
    public void AddIAMServiceClient_ThenAuthServiceIAMClient_RejectsMixedAuthenticationBeforeMutation()
    {
        var builder = CreateConfiguredExchangeBuilder();
        builder.AddIAMServiceClient("legacy-search");
        builder.AddAuthServiceTokenExchange(ServiceName);
        var descriptorCount = builder.Services.Count;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddAuthServiceIAMClient());

        Assert.Contains("cannot be combined", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(descriptorCount, builder.Services.Count);
    }

    /// <summary>Verifies an existing legacy registration wins over invalid central-client configuration.</summary>
    [Fact]
    public void AddIAMServiceClient_ThenMisconfiguredAuthServiceIAMClient_ReportsAuthenticationConflict()
    {
        var builder = CreateConfiguredExchangeBuilder();
        builder.AddIAMServiceClient("legacy-search");
        builder.AddAuthServiceTokenExchange(ServiceName);
        builder.Configuration["Services:IAMService:BaseUrl"] = "http://iam.internal";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddAuthServiceIAMClient());

        Assert.Contains("cannot be combined", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies an arbitrary provider registration cannot impersonate token-exchange setup.</summary>
    [Fact]
    public void AddAuthServiceIAMClient_StubProviderWithoutExchangeMarker_FailsImmediately()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IAuthServiceTokenProvider>(new StubTokenProvider("untrusted"));
        var descriptorCount = builder.Services.Count;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddAuthServiceIAMClient());

        Assert.Contains("AddAuthServiceTokenExchange", exception.Message, StringComparison.Ordinal);
        Assert.Equal(descriptorCount, builder.Services.Count);
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IIamServiceClient));
    }

    /// <summary>Verifies the IAM client sends the AuthService token through the opt-in handler chain.</summary>
    [Fact]
    public async Task AddAuthServiceIAMClient_PermissionRequest_UsesCentralExchangeBearerToken()
    {
        var token = CreateServiceToken();
        HttpRequestMessage? capturedRequest = null;
        var terminal = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    principalId = "user-1",
                    permissions = new[] { "search.documents.read" },
                    roles = Array.Empty<string>(),
                    cacheUntil = DateTimeOffset.UtcNow,
                    fromCache = false
                })
            });
        });
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:IAMService:BaseUrl"] = "https://iam.test"
        });
        builder.AddAuthServiceTokenExchange(ServiceName);
        builder.AddAuthServiceIAMClient();
        builder.Services.RemoveAll<IAuthServiceTokenProvider>();
        builder.Services.AddSingleton<IAuthServiceTokenProvider>(new StubTokenProvider(token));
        builder.Services.AddHttpClient("IAMService")
            .ConfigurePrimaryHttpMessageHandler(() => terminal);
        using var provider = builder.Services.BuildServiceProvider();

        var permissions = await provider.GetRequiredService<IIamServiceClient>()
            .GetUserPermissionsAsync("user-1");

        Assert.Equal(["search.documents.read"], permissions);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", token), capturedRequest!.Headers.Authorization);
        Assert.Equal("/iam/v1/auth/resolve-permissions", capturedRequest.RequestUri!.AbsolutePath);
    }

    private AuthServiceTokenProvider CreateProvider(
        StubHttpMessageHandler transport,
        int refreshSafetyMarginSeconds = 60,
        string? publicKey = null)
    {
        var httpClient = new HttpClient(transport)
        {
            BaseAddress = new Uri("https://auth.test")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:PublicKey"] = publicKey ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience
            })
            .Build();
        var options = Options.Create(new AuthServiceTokenExchangeOptions
        {
            ClientId = ClientId,
            ClientSecret = ClientSecret,
            RefreshSafetyMarginSeconds = refreshSafetyMarginSeconds
        });

        return new AuthServiceTokenProvider(
            new StubHttpClientFactory(httpClient),
            options,
            new ServiceProcessIdentity(ServiceName),
            configuration,
            _time,
            NullLogger<AuthServiceTokenProvider>.Instance);
    }

    private HostApplicationBuilder CreateConfiguredExchangeBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = ClientId,
            ["ServiceAuthentication:ClientSecret"] = ClientSecret,
            ["Services:AuthService:BaseUrl"] = "https://auth.test",
            ["Services:IAMService:BaseUrl"] = "https://iam.test",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience
        });
        return builder;
    }

    private static object? GetActiveRefresh(AuthServiceTokenProvider provider) =>
        typeof(AuthServiceTokenProvider)
            .GetField("_activeRefresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(provider);

    private IHost CreateExchangeHost(
        string publicKey,
        string issuer = Issuer,
        string audience = Audience,
        string? authServiceBaseUrl = null,
        string environmentName = "Production")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuthentication:ClientId"] = ClientId,
            ["ServiceAuthentication:ClientSecret"] = ClientSecret,
            ["Jwt:PublicKey"] = publicKey,
            ["Jwt:Issuer"] = issuer,
            ["Jwt:Audience"] = audience
        });
        if (authServiceBaseUrl is not null)
        {
            builder.Configuration["Services:AuthService:BaseUrl"] = authServiceBaseUrl;
        }

        builder.AddAuthServiceTokenExchange(ServiceName);
        return builder.Build();
    }

    private StubHttpMessageHandler RespondWith(Func<HttpResponseMessage> responseFactory) =>
        new((_, _) => Task.FromResult(responseFactory()));

    private HttpResponseMessage CreateSuccessResponse(
        string token,
        int expiresIn = 900,
        string responseClientId = ClientId,
        string responseServiceName = ServiceName,
        string responsePrincipalId = "11111111-1111-1111-1111-111111111111")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = token,
                token_type = "Bearer",
                expires_in = expiresIn,
                user = new
                {
                    user_id = responseClientId,
                    principal_id = responsePrincipalId,
                    user_type = "service",
                    name = responseServiceName
                }
            })
        };
    }

    private string CreateServiceToken(
        string clientId = ClientId,
        string serviceName = ServiceName,
        int lifetimeSeconds = 900,
        string? jti = null,
        IReadOnlyCollection<Claim>? additionalClaims = null,
        RSA? signingKey = null,
        TimeSpan? notBeforeOffset = null,
        TimeSpan? issuedAtOffset = null,
        string subject = "11111111-1111-1111-1111-111111111111")
    {
        var now = _time.GetUtcNow();
        var key = new RsaSecurityKey(signingKey ?? _rsa) { KeyId = "test-key" };
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, jti ?? Guid.NewGuid().ToString()),
            new(
                JwtRegisteredClaimNames.Iat,
                now.Add(issuedAtOffset ?? TimeSpan.Zero).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("client_id", clientId),
            new("service_name", serviceName),
            new("user_type", "service"),
            new("permissions", "iam.auth.resolve-permissions")
        };
        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.Add(notBeforeOffset ?? TimeSpan.Zero).UtcDateTime,
            expires: now.AddSeconds(lifetimeSeconds).UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubTokenProvider(string token) : IAuthServiceTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(token);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
