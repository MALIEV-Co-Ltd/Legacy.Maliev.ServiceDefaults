using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class PrivateHttpTransportAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_standard_pipeline_timeout_emits_one_terminal_event_without_invented_status(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Mode = Outcome.Block;

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => fixture.Client.GetAsync("/private-timeout-sentinel"));

        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(3, fixture.Transport.Cancellations);
        fixture.AssertUnknownFailure();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_flight_caller_cancellation_is_quiet_without_retry(bool typed)
    {
        using var fixture = new Fixture(typed);
        using var warm = await fixture.Client.GetAsync("/warm");
        fixture.Transport.Mode = Outcome.Block;
        using var cancellation = new CancellationTokenSource();
        var call = fixture.Client.GetAsync("/private-cancel-sentinel", cancellation.Token);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Equal(1, fixture.Transport.Cancellations);
        Assert.Empty(fixture.Events.Failures);
        Assert.Equal(1, fixture.Exchange.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_transport_status_stays_absent_and_original_exception_survives(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Mode = Outcome.Throw;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.PostAsync("/private-url-sentinel", new StringContent("private-body-sentinel")));

        Assert.Same(fixture.Transport.OriginalFailure, failure);
        Assert.Null(failure.StatusCode);
        Assert.Equal(1, fixture.Transport.Calls);
        fixture.AssertUnknownFailure();
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    public async Task Rejected_bearer_keeps_response_and_real_cache_invalidation(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        using var warm = await fixture.Client.GetAsync("/warm");
        fixture.Transport.Mode = status == 401 ? Outcome.Unauthorized : Outcome.Forbidden;

        using var rejected = await fixture.Client.PostAsync("/write", new StringContent("bounded"));

        Assert.Same(fixture.Transport.LastResponse, rejected);
        Assert.Equal((HttpStatusCode)status, rejected.StatusCode);
        fixture.Transport.Mode = Outcome.Ok;
        using var refreshed = await fixture.Client.GetAsync("/after-rejection");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(2, fixture.Exchange.Calls);
        Assert.Equal(new[] { "Bearer " + fixture.FirstToken, "Bearer " + fixture.FirstToken, "Bearer " + fixture.SecondToken }, fixture.Transport.Bearers);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opt_in_before_Defaults_stays_outside_real_auth_and_resilience(bool typed)
    {
        using var fixture = new Fixture(typed, beforeDefaults: true);
        fixture.Transport.Mode = Outcome.Unavailable;

        using var response = await fixture.Client.GetAsync("/read");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Single(fixture.Events.Failures);
        var chain = fixture.Chain();
        int observer = chain.IndexOf("PrivateDependencyFailureHandler");
        Assert.True(observer >= 0 && observer < chain.IndexOf(nameof(LegacyServiceAuthenticationHandler)));
        Assert.True(observer < chain.IndexOf("ResilienceHandler"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Custom_forwarding_filter_order_is_characterized_not_universal_outermost(bool typed, bool customFirst)
    {
        using var fixture = new Fixture(typed, customFirst: customFirst);
        fixture.Transport.Mode = Outcome.Unavailable;

        using var response = await fixture.Client.PostAsync("/write", new StringContent("bounded"));

        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Single(fixture.Events.Failures);
        var chain = fixture.Chain();
        int observer = chain.IndexOf("PrivateDependencyFailureHandler");
        int custom = chain.IndexOf(nameof(OwnedForwardingHandler));
        Assert.True(observer >= 0 && custom >= 0);
        Assert.Equal(customFirst, custom < observer);
    }

    private enum Outcome { Ok, Block, Throw, Unauthorized, Forbidden, Unavailable }

    private sealed class Fixture : IDisposable
    {
        public string FirstToken { get; } = Guid.NewGuid().ToString("N");
        public string SecondToken { get; } = Guid.NewGuid().ToString("N");
        public Transport Transport { get; } = new();
        public Events Events { get; } = new();
        public LoginTransport Exchange { get; }
        public IHost Host { get; }
        public HttpClient Client { get; }
        public Fixture(bool typed, bool beforeDefaults = false, bool? customFirst = null)
        {
            string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            Exchange = new(FirstToken, SecondToken, secret);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
            builder.Configuration["Services:Auth:BaseUrl"] = "https://auth.example.invalid";
            builder.Configuration["ServiceAuthentication:ClientId"] = "legacy-quotation";
            builder.Configuration["ServiceAuthentication:ClientSecret"] = secret;
            builder.Logging.AddProvider(Events);
            if (!beforeDefaults) builder.AddServiceDefaults();
            builder.AddLegacyAuthServiceTokenExchange();
            builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Exchange);
            var client = typed
                ? builder.Services.AddHttpClient<TransportClient>("TransportProof", value => value.BaseAddress = new Uri("https://dependency.example.invalid"))
                : builder.Services.AddHttpClient("TransportProof", value => value.BaseAddress = new Uri("https://dependency.example.invalid"));
            client.ConfigurePrimaryHttpMessageHandler(() => Transport).AddLegacyServiceAuthentication();
            if (customFirst is true) builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter, OwnedForwardingFilter>();
            client.AddPrivateFailureObservation("ControlledDependency");
            if (customFirst is false) builder.Services.AddSingleton<IHttpMessageHandlerBuilderFilter, OwnedForwardingFilter>();
            if (beforeDefaults) builder.AddServiceDefaults();
            builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
                options.Retry.MaxRetryAttempts = 2;
            });
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<TransportClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("TransportProof");
        }
        public List<string> Chain()
        {
            var result = new List<string>();
            HttpMessageHandler current = Host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("TransportProof");
            while (current is DelegatingHandler handler)
            {
                result.Add(current.GetType().Name);
                current = Assert.IsAssignableFrom<HttpMessageHandler>(handler.InnerHandler);
            }
            return result;
        }
        public void AssertUnknownFailure()
        {
            var entry = Assert.Single(Events.Failures);
            Assert.Null(entry.Exception);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal("ControlledDependency", entry.Fields["Dependency"]);
            Assert.False(entry.Fields.ContainsKey("StatusCode"));
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
            var log = new LogEntry<IReadOnlyDictionary<string, object?>>(entry.Level, "Controlled.Transport", new EventId(5101),
                entry.Fields, entry.Exception, (_, _) => entry.Message);
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(log, null, writer);
            using var json = JsonDocument.Parse(writer.ToString());
            Assert.False(json.RootElement.TryGetProperty("StatusCode", out _));
            Assert.DoesNotContain("private-", writer.ToString(), StringComparison.Ordinal);
        }
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
    public sealed class TransportClient(HttpClient client) { public HttpClient Client { get; } = client; }
    private sealed class Transport : HttpMessageHandler
    {
        public Outcome Mode { get; set; }
        public int Calls { get; private set; }
        public int Cancellations { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpRequestException OriginalFailure { get; } = new("private-transport-sentinel");
        public List<string?> Bearers { get; } = [];
        public HttpResponseMessage? LastResponse { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Bearers.Add(request.Headers.Authorization?.ToString());
            if (Mode == Outcome.Throw) throw OriginalFailure;
            if (Mode == Outcome.Block)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancellations++; throw; }
            }
            LastResponse = new(Mode switch
            {
                Outcome.Unauthorized => HttpStatusCode.Unauthorized,
                Outcome.Forbidden => HttpStatusCode.Forbidden,
                Outcome.Unavailable => HttpStatusCode.ServiceUnavailable,
                _ => HttpStatusCode.OK
            })
            { RequestMessage = request, Content = new StringContent("private-response-sentinel") };
            return LastResponse;
        }
    }
    private sealed class LoginTransport(string first, string second, string secret) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://auth.example.invalid/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("legacy-quotation", body.RootElement.GetProperty("clientId").GetString());
            Assert.Equal(secret, body.RootElement.GetProperty("clientSecret").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = Calls == 1 ? first : second, expiresIn = 900 })) };
        }
    }
    private sealed class OwnedForwardingFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name == "TransportProof") builder.AdditionalHandlers.Insert(0, new OwnedForwardingHandler());
        };
    }
    private sealed class OwnedForwardingHandler : DelegatingHandler;
    private sealed record Event(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);
    private sealed class Events : ILoggerProvider
    {
        public ConcurrentQueue<Event> Failures { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Failures);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<Event> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                var values = fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
                if (values.GetValueOrDefault("EventName") as string == "DependencyRequestFailure")
                    events.Enqueue(new(level, formatter(state, error), error, values));
            }
        }
    }
}
