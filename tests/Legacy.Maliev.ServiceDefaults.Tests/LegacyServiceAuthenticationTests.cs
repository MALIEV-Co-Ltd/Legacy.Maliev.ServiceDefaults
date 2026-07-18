using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Compatibility and resilience tests for legacy service authentication.</summary>
public sealed class LegacyServiceAuthenticationTests
{
    private readonly ManualTimeProvider time = new(DateTimeOffset.Parse("2026-07-18T00:00:00Z"));

    /// <summary>Verifies the legacy camel-case login contract and shared concurrent refresh.</summary>
    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_ExchangeOnceUsingLegacyContract()
    {
        string? body = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new StubHandler(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            await release.Task.WaitAsync(cancellationToken);
            return Json(HttpStatusCode.OK, "{\"accessToken\":\"short-lived-token\",\"expiresIn\":900}");
        });
        var provider = CreateProvider(transport);

        var callers = Enumerable.Range(0, 16).Select(_ => provider.GetAccessTokenAsync().AsTask()).ToArray();
        await WaitUntilAsync(() => transport.RequestCount == 1);
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Single(tokens.Distinct(StringComparer.Ordinal));
        Assert.Contains("\"clientId\":\"legacy-quotation\"", body, StringComparison.Ordinal);
        Assert.Contains("\"clientSecret\":\"runtime-only-secret\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", body, StringComparison.Ordinal);
    }

    /// <summary>Verifies a rejected token is invalidated and refreshed on the next request.</summary>
    [Fact]
    public async Task SendAsync_UnauthorizedResponse_InvalidatesCachedToken()
    {
        var tokenNumber = 0;
        var transport = new StubHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK,
            $"{{\"accessToken\":\"token-{Interlocked.Increment(ref tokenNumber)}\",\"expiresIn\":900}}")));
        var provider = CreateProvider(transport);
        var downstream = new StubHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(request.Headers.Authorization?.Parameter == "token-1"
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.OK)));
        var handler = new LegacyServiceAuthenticationHandler(provider) { InnerHandler = downstream };
        using var client = new HttpClient(handler);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("https://orders.test/one")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://orders.test/two")).StatusCode);
        Assert.Equal(2, transport.RequestCount);
    }

    /// <summary>Verifies absent runtime credentials fail closed before any network request.</summary>
    [Fact]
    public async Task SendAsync_MissingCredentials_FailsClosedWithoutNetworkCall()
    {
        var transport = new StubHandler((_, _) => throw new InvalidOperationException("must not call"));
        var provider = CreateProvider(transport, clientSecret: string.Empty);
        var handler = new LegacyServiceAuthenticationHandler(provider)
        {
            InnerHandler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        };
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://orders.test/"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.DoesNotContain("legacy-quotation", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, transport.RequestCount);
    }

    /// <summary>Verifies tokens are refreshed before their effective expiry.</summary>
    [Fact]
    public async Task GetAccessTokenAsync_RefreshesAtBoundedSafetyMargin()
    {
        var issued = 0;
        var transport = new StubHandler((_, _) => Task.FromResult(Json(
            HttpStatusCode.OK,
            $"{{\"accessToken\":\"token-{Interlocked.Increment(ref issued)}\",\"expiresIn\":300}}")));
        var provider = CreateProvider(transport);

        var first = await provider.GetAccessTokenAsync();
        time.Advance(TimeSpan.FromSeconds(239));
        var cached = await provider.GetAccessTokenAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await provider.GetAccessTokenAsync();

        Assert.Equal(first, cached);
        Assert.NotEqual(first, refreshed);
    }

    private LegacyServiceAccessTokenProvider CreateProvider(StubHandler transport, string clientSecret = "runtime-only-secret") =>
        new(
            new SingleClientFactory(new HttpClient(transport) { BaseAddress = new Uri("https://auth.test") }),
            Options.Create(new LegacyServiceAuthenticationOptions
            {
                ClientId = "legacy-quotation",
                ClientSecret = clientSecret
            }),
            time,
            NullLogger<LegacyServiceAccessTokenProvider>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 200 && !predicate(); index++) await Task.Delay(5);
        Assert.True(predicate());
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int requests;
        public int RequestCount => Volatile.Read(ref requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return send(request, cancellationToken);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
