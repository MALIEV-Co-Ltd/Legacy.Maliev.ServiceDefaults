using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Normal registered workload exchange with controlled transport stalls and real consumer handlers.</summary>
public sealed class LegacyTokenExchangeDeadlineTests
{
    /// <summary>Bounds the shared owner, releases failed refreshes, and disposes late transport results once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whole_exchange_deadline_releases_waiters_and_retry_without_caching_late_result(bool stallHeaders)
    {
        using var fixture = new Fixture(stallHeaders, false, TimeSpan.FromMilliseconds(200));
        var calls = Enumerable.Range(0, 8).Select(_ => fixture.Client.GetAsync("/consume")).ToArray();
        try
        {
            await fixture.Exchange.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var call in calls)
            {
                var failure = await Assert.ThrowsAsync<HttpRequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            }
            Assert.Equal(1, fixture.Exchange.Requests);
            Assert.Equal(0, fixture.Downstream.Requests);
            using var recovered = await fixture.Client.GetAsync("/consume").WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(2, fixture.Exchange.Requests);
            Assert.Equal("fresh-token", fixture.Downstream.LastBearer);

            fixture.Exchange.Release.TrySetResult();
            await fixture.Exchange.LateContent.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, fixture.Exchange.LateContent.Disposals);
            using var cached = await fixture.Client.GetAsync("/consume");
            Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
            Assert.Equal("fresh-token", fixture.Downstream.LastBearer);
            Assert.Equal(2, fixture.Exchange.Requests);
        }
        finally
        {
            fixture.Exchange.Release.TrySetResult();
            foreach (var call in calls)
            {
                try { (await call.WaitAsync(TimeSpan.FromSeconds(3))).Dispose(); }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutException or OperationCanceledException) { }
            }
        }
    }

    /// <summary>Caller cancellation does not cancel the owner; successful coalescing and cache expiry remain intact.</summary>
    [Fact]
    public async Task Caller_cancellation_preserves_shared_refresh_and_live_cache_expiry()
    {
        using var fixture = new Fixture(true, false, TimeSpan.FromSeconds(3));
        using var caller = new CancellationTokenSource();
        var canceled = fixture.Provider.GetAccessTokenAsync(caller.Token).AsTask();
        var survivors = Enumerable.Range(0, 8).Select(_ => fixture.Provider.GetAccessTokenAsync().AsTask()).ToArray();
        try
        {
            await fixture.Exchange.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.False(fixture.Exchange.HeaderCancellation.IsCancellationRequested);
            fixture.Exchange.Release.TrySetResult();
            Assert.All(await Task.WhenAll(survivors).WaitAsync(TimeSpan.FromSeconds(3)), token => Assert.Equal("late-token", token));
            Assert.Equal(1, fixture.Exchange.Requests);
            Assert.Equal("late-token", await fixture.Provider.GetAccessTokenAsync());
            fixture.Time.Advance(TimeSpan.FromSeconds(781));
            Assert.Equal("fresh-token", await fixture.Provider.GetAccessTokenAsync());
            Assert.Equal(2, fixture.Exchange.Requests);
        }
        finally
        {
            fixture.Exchange.Release.TrySetResult();
            await Task.WhenAll(survivors).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>The same owner deadline reaches body reads and prevents a stalled response from occupying refresh forever.</summary>
    [Fact]
    public async Task Owner_deadline_cancels_cooperative_body_and_disposes_response()
    {
        using var fixture = new Fixture(false, true, TimeSpan.FromMilliseconds(200));
        try
        {
            Assert.Null(await fixture.Provider.GetAccessTokenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            await fixture.Exchange.LateContent.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Exchange.Body.CancellationObserved);
            Assert.Equal(1, fixture.Exchange.LateContent.Disposals);
            Assert.Equal("fresh-token", await fixture.Provider.GetAccessTokenAsync());
            Assert.Equal(2, fixture.Exchange.Requests);
        }
        finally { fixture.Exchange.Release.TrySetResult(); }
    }

    private sealed class Fixture : IDisposable
    {
        public IHost Host { get; }
        public HttpClient Client { get; }
        public ILegacyServiceAccessTokenProvider Provider { get; }
        public ExchangeTransport Exchange { get; }
        public ConsumerTransport Downstream { get; } = new();
        public ManualTimeProvider Time { get; } = new();

        public Fixture(bool stallHeaders, bool cooperativeBody, TimeSpan timeout)
        {
            Exchange = new(stallHeaders, cooperativeBody);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
            builder.Configuration["ServiceAuthentication:ClientId"] = "legacy-quotation";
            builder.Configuration["ServiceAuthentication:ClientSecret"] = Guid.NewGuid().ToString("N");
            builder.Configuration["Services:Auth:BaseUrl"] = "https://auth.example.invalid";
            builder.Services.AddSingleton<TimeProvider>(Time);
            builder.AddServiceDefaults();
            builder.AddLegacyAuthServiceTokenExchange();
            builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName, client => client.Timeout = timeout)
                .ConfigurePrimaryHttpMessageHandler(() => Exchange);
            builder.Services.AddHttpClient("DeadlineConsumer", client => client.BaseAddress = new Uri("https://consumer.example.invalid"))
                .ConfigurePrimaryHttpMessageHandler(() => Downstream)
                .AddLegacyServiceAuthentication();
            Host = builder.Build();
            Provider = Host.Services.GetRequiredService<ILegacyServiceAccessTokenProvider>();
            Client = Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("DeadlineConsumer");
        }

        public void Dispose()
        {
            Exchange.Release.TrySetResult();
            Client.Dispose();
            Host.Dispose();
        }
    }

    private sealed class ExchangeTransport : HttpMessageHandler
    {
        private readonly bool stallHeaders;
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeferredBody Body { get; }
        public TrackingContent LateContent { get; }
        public CancellationToken HeaderCancellation { get; private set; }

        public ExchangeTransport(bool stallHeaders, bool cooperativeBody)
        {
            this.stallHeaders = stallHeaders;
            Body = new(Release.Task, cooperativeBody, stallHeaders);
            LateContent = new(Body);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/auth/v1/service/login", request.RequestUri!.AbsolutePath);
            if (Interlocked.Increment(ref requests) != 1)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"fresh-token\",\"expiresIn\":900}", Encoding.UTF8, "application/json") };
            HeaderCancellation = cancellationToken;
            Entered.TrySetResult();
            if (stallHeaders) await Release.Task;
            return new(HttpStatusCode.OK) { Content = LateContent };
        }
    }

    private sealed class ConsumerTransport : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastBearer { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TrackingContent(Stream stream) : StreamContent(stream)
    {
        private int disposals;
        public int Disposals => Volatile.Read(ref disposals);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref disposals);
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }

    private sealed class DeferredBody(Task release, bool cooperative, bool immediate) : Stream
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes("{\"accessToken\":\"late-token\",\"expiresIn\":900}");
        private int offset;
        public bool CancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!immediate)
            {
                try { if (cooperative) await release.WaitAsync(cancellationToken); else await release; }
                catch (OperationCanceledException) { CancellationObserved = true; throw; }
            }
            var count = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-05T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
