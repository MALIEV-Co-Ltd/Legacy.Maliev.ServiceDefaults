using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Characterizes local-signing client wiring; not live IAM authorization acceptance.
public sealed class AuthenticatedServiceClientBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredIamClient_SignsActualRequestAndPreservesServiceHeader(bool directRegistration)
    {
        using var rig = new ClientRig(directRegistration: directRegistration);
        using var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-user-token");

        using var response = await client.GetAsync("/fixture/check");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        var captured = Assert.Single(rig.Transport.Requests);
        Assert.Equal("https://iam.example.test/fixture/check", captured.Target);
        Assert.Equal("FixtureSourceService", captured.ServiceName);
        Assert.Equal("Bearer", captured.Scheme);
        rig.AssertToken(captured.Token, "https://issuer.example.test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredTypedClient_PreservesIamConfigurationAndSignsItsOwnRequest(bool explicitAddress)
    {
        using var rig = new ClientRig(explicitTypedAddress: explicitAddress);
        using var iam = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        var typed = rig.Host.Services.GetRequiredService<IReviewClient>();
        using var typedClient = typed.Client;

        Assert.Equal("https://iam.example.test/", iam.BaseAddress!.AbsoluteUri);
        Assert.Equal(explicitAddress ? "https://review.example.test/" : "https+http://reviewservice/", typedClient.BaseAddress!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(90), typedClient.Timeout);
        // Absolute benign URI avoids requiring live service discovery for this transport assertion.
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://review.example.test/fixture/review");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-user-token");
        using var response = await typedClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var captured = Assert.Single(rig.Transport.Requests);
        Assert.Equal("https://review.example.test/fixture/review", captured.Target);
        Assert.Null(captured.ServiceName);
        rig.AssertToken(captured.Token, "https://issuer.example.test");
    }

    [Fact]
    public async Task ReusedRegisteredClient_ReReadsSigningConfigurationForEachRequest()
    {
        using var rig = new ClientRig();
        using var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        using var first = await client.GetAsync("/fixture/first");
        rig.Configuration["Jwt:Issuer"] = "https://changed-issuer.example.test";
        using var second = await client.GetAsync("/fixture/second");

        Assert.Equal(2, rig.Transport.Requests.Count);
        rig.AssertToken(rig.Transport.Requests[0].Token, "https://issuer.example.test");
        rig.AssertToken(rig.Transport.Requests[1].Token, "https://changed-issuer.example.test");
    }

    [Fact]
    public async Task RegisteredClient_MissingProductionSigningMaterialFailsBeforeOutbound()
    {
        using var rig = new ClientRig();
        rig.Configuration["Jwt:PrivateKey"] = null;
        using var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/fixture/check"));

        Assert.Empty(rig.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredClient_PropagatesTransportFailureWithoutRetry(bool unexpected)
    {
        using var rig = new ClientRig(send: (_, _) => unexpected
            ? Task.FromException<HttpResponseMessage>(new InvalidOperationException("synthetic transport failure"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic transport failure")));
        using var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");

        if (unexpected)
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/fixture/check"));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/fixture/check"));

        rig.AssertToken(Assert.Single(rig.Transport.Requests).Token, "https://issuer.example.test");
    }

    [Fact]
    public async Task RegisteredClient_CallerCancellationReachesTransportAndDrainsRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new ClientRig(send: async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var client = rig.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync("/fixture/check", cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(rig.Transport.Requests);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    public interface IReviewClient { HttpClient Client { get; } }
    public sealed class ReviewClient(HttpClient client) : IReviewClient { public HttpClient Client { get; } = client; }

    private sealed class ClientRig : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public IConfiguration Configuration { get; }
        public IHost Host { get; }
        public RecordingTransport Transport { get; }

        public ClientRig(bool directRegistration = false, bool explicitTypedAddress = true,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ServiceName"] = "FixtureSourceService",
                ["Jwt:Issuer"] = "https://issuer.example.test",
                ["Jwt:Audience"] = "https://audience.example.test",
                ["Jwt:PrivateKey"] = _rsa.ExportPkcs8PrivateKeyPem(),
                ["Services:IAMService:BaseUrl"] = "https://iam.example.test",
                ["Services:ReviewService:BaseUrl"] = explicitTypedAddress ? "https://review.example.test" : null
            });
            Configuration = builder.Configuration;
            Transport = new RecordingTransport(send);
            // Client-level discovery installs the handler, not its endpoint providers.
            // Mirror the host's normal provider registration rather than bypassing it.
            builder.Services.AddServiceDiscovery();
            if (directRegistration)
                builder.Services.AddIAMClient(builder.Configuration, "FixtureSourceService");
            else
                builder.AddIAMServiceClient();
            builder.Services.AddHttpClient("IAMService").ConfigurePrimaryHttpMessageHandler(() => Transport);
            builder.AddAuthenticatedServiceClient<IReviewClient, ReviewClient>("ReviewService")
                .ConfigurePrimaryHttpMessageHandler(() => Transport);
            Host = builder.Build();
        }

        public void AssertToken(string? token, string issuer)
        {
            Assert.True(!string.IsNullOrEmpty(token), "The transport must receive a signed token.");
            SecurityToken? validated = null;
            try
            {
                new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token!, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(_rsa),
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = "https://audience.example.test",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                }, out validated);
            }
            catch (SecurityTokenException) { Assert.Fail("The transport token failed signature or identity validation."); }
            var jwt = Assert.IsType<JwtSecurityToken>(validated);
            Assert.Equal("FixtureSourceService", Assert.Single(jwt.Claims, c => c.Type == "service_name").Value);
            Assert.Equal("system:service:fixturesource", Assert.Single(jwt.Claims, c => c.Type == "sub").Value);
            Assert.Equal("service", Assert.Single(jwt.Claims, c => c.Type == "user_type").Value);
            Assert.Equal("iam-registration", Assert.Single(jwt.Claims, c => c.Type == "purpose").Value);
            Assert.Equal("service-account", Assert.Single(jwt.Claims, c => c.Type == "role").Value);
        }

        public void Dispose() { Host.Dispose(); Transport.Dispose(); _rsa.Dispose(); }
    }

    private sealed record CapturedRequest(string Target, string? ServiceName, string? Scheme, string? Token);
    private sealed class RecordingTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-Service-Name", out var values) ? Assert.Single(values) : null,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return send?.Invoke(request, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}
