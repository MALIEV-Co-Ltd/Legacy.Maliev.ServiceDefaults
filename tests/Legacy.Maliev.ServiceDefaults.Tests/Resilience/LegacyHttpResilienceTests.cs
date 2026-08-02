using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Maliev.Aspire.Tests.Infrastructure;

public sealed class LegacyHttpResilienceTests
{
    [Fact]
    public void ConfigureLegacyStandardResilience_UsesBoundedSharedDefaults()
    {
        var options = new HttpStandardResilienceOptions();

        LegacyHttpResilienceExtensions.ConfigureLegacyStandardResilience(options);

        Assert.Equal(TimeSpan.FromSeconds(30), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(65), options.CircuitBreaker.SamplingDuration);
    }

    [Fact]
    public async Task Get_TransientFailuresAreRetried()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var provider = BuildProvider(handler);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy-test");

        using var response = await client.GetAsync("/read-only");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("CONNECT")]
    public async Task UnsafeMethods_DoNotRetryTransientFailures(string method)
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var provider = BuildProvider(handler);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy-test");

        using var request = new HttpRequestMessage(new HttpMethod(method), "/write");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CustomRetrySettings_CannotReenableUnsafeMethodRetries()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        var services = new ServiceCollection();
        services.AddHttpClient("legacy-test", client => client.BaseAddress = new Uri("https://legacy.test"))
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddLegacyStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 5;
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            });

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy-test");
        using var response = await client.PostAsync("/write", new StringContent("payload"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedToTheDownstreamHandler()
    {
        using var handler = new BlockingHandler();
        using var provider = BuildProvider(handler);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("legacy-test");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/slow", cancellation.Token));

        Assert.True(handler.WasCancelled);
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("legacy-test", client => client.BaseAddress = new Uri("https://legacy.test"))
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddLegacyStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            });

        return services.BuildServiceProvider();
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpStatusCode> _statuses = new(statuses);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            _statuses.TryDequeue(out var status);
            return Task.FromResult(new HttpResponseMessage(status == default ? HttpStatusCode.OK : status)
            {
                RequestMessage = request
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public bool WasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        }
    }
}
