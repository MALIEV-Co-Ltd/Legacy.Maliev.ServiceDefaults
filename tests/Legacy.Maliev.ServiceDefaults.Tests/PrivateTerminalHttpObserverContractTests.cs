using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class PrivateTerminalHttpObserverContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_factory_observes_only_terminal_safe_read_outcome(bool typed, bool recovered)
    {
        using var fixture = new Fixture(typed, recovered);

        using var response = await fixture.Client.GetAsync("/private-path-sentinel?token=private-query-sentinel");

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(recovered ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(1, fixture.Exchange.Calls);
        Assert.Equal(recovered ? 0 : 1, fixture.Events.Failures.Count);
        fixture.AssertWireAndPrivacy();
    }

    [Theory]
    [InlineData(false, "POST")]
    [InlineData(true, "POST")]
    [InlineData(false, "PUT")]
    [InlineData(true, "PUT")]
    public async Task Unsafe_write_is_not_retried_and_response_identity_is_preserved(bool typed, string method)
    {
        using var fixture = new Fixture(typed, false);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/private-path-sentinel")
        {
            Content = new StringContent("private-body-sentinel")
        };

        using var response = await fixture.Client.SendAsync(request);

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Equal("private-body-sentinel", fixture.Transport.Body);
        Assert.Equal(method, fixture.Transport.Method);
        Assert.Single(fixture.Events.Failures);
        fixture.AssertWireAndPrivacy();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_is_quiet_and_preserved(bool typed)
    {
        using var fixture = new Fixture(typed, false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GetAsync("/read", cancellation.Token));

        Assert.Empty(fixture.Events.Failures);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Repeated_opt_in_and_auth_registration_order_do_not_duplicate_observation(bool typed, bool beforeAuth)
    {
        using var fixture = new Fixture(typed, false, beforeAuth, repeat: true);

        using var response = await fixture.Client.PostAsync("/write", new StringContent("bounded"));

        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Single(fixture.Events.Failures);
        fixture.AssertWireAndPrivacy();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_selection_leaves_other_factory_client_unobserved(bool typed)
    {
        using var fixture = new Fixture(typed, false);
        using var other = fixture.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Unselected");

        using var response = await other.PostAsync("/write", new StringContent("bounded"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(fixture.Events.Failures);
        Assert.Equal(1, fixture.Other.Calls);
        Assert.Equal(0, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_credentials_keep_real_auth_failure_and_zero_dependency_calls(bool typed)
    {
        using var fixture = new Fixture(typed, false, missingCredentials: true);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Client.PostAsync("/write", new StringContent("bounded")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Equal(0, fixture.Exchange.Calls);
        Assert.Single(fixture.Events.Failures);
        fixture.AssertPrivacy();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Throwing_diagnostic_provider_cannot_replace_response_or_original_transport_failure(bool typed, bool transportFailure)
    {
        using var fixture = new Fixture(typed, false);
        fixture.Events.ThrowOnFailure = true;
        fixture.Transport.ThrowTransportFailure = transportFailure;

        if (transportFailure)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(
                () => fixture.Client.PostAsync("/write", new StringContent("bounded")));
            Assert.Same(fixture.Transport.OriginalFailure, failure);
        }
        else
        {
            using var response = await fixture.Client.PostAsync("/write", new StringContent("bounded"));
            Assert.Same(fixture.Transport.LastResponse, response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Single(fixture.Events.Failures);
    }

    private static void SelectObservation(IHttpClientBuilder client)
        => InvokeSelection(client, "ControlledDependency");

    private static void InvokeSelection(IHttpClientBuilder client, string dependency)
    {
        var type = typeof(PrivateFailureConsoleFormatter).Assembly.GetType(
            "Microsoft.Extensions.Hosting.PrivateFailureObservationExtensions");
        var method = type?.GetMethod("AddPrivateFailureObservation", BindingFlags.Public | BindingFlags.Static,
            [typeof(IHttpClientBuilder), typeof(string)]);
        Assert.True(method is not null, "Explicit per-client terminal failure observer is not implemented.");
        var result = method!.Invoke(null, [client, dependency]);
        Assert.Same(client, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("private-dependency-sentinel/path")]
    [InlineData("private-dependency-sentinel@example.invalid")]
    public void Invalid_dependency_identity_is_rejected_without_echo_or_registration(string dependency)
    {
        var services = new ServiceCollection();
        var client = services.AddHttpClient("Guarded");
        int original = services.Count;

        var wrapper = Assert.Throws<TargetInvocationException>(() => InvokeSelection(client, dependency));

        var failure = Assert.IsType<ArgumentException>(wrapper.InnerException);
        Assert.DoesNotContain("private-", failure.Message, StringComparison.Ordinal);
        Assert.Equal(original, services.Count);
    }

    [Fact]
    public void Conflicting_dependency_identity_rejects_second_selection_without_mutating_first()
    {
        var services = new ServiceCollection();
        var client = services.AddHttpClient("Guarded");
        InvokeSelection(client, "ControlledDependency");
        int original = services.Count;

        var wrapper = Assert.Throws<TargetInvocationException>(() => InvokeSelection(client, "DifferentDependency"));

        Assert.IsType<InvalidOperationException>(wrapper.InnerException);
        Assert.Equal(original, services.Count);
        InvokeSelection(client, "ControlledDependency");
        Assert.Equal(original, services.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_built_handler_chain_places_single_observer_outside_auth_and_resilience(bool typed)
    {
        using var fixture = new Fixture(typed, false);
        HttpMessageHandler current = fixture.Host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("Observed");
        var chain = new List<string>();
        while (current is DelegatingHandler delegating)
        {
            chain.Add(current.GetType().Name);
            current = Assert.IsAssignableFrom<HttpMessageHandler>(delegating.InnerHandler);
        }

        Assert.Single(chain, name => name == "PrivateDependencyFailureHandler");
        Assert.Single(chain, name => name == nameof(LegacyServiceAuthenticationHandler));
        Assert.Contains("ResilienceHandler", chain);
        int observer = chain.IndexOf("PrivateDependencyFailureHandler");
        Assert.True(observer < chain.IndexOf(nameof(LegacyServiceAuthenticationHandler)));
        Assert.True(observer < chain.IndexOf("ResilienceHandler"));
    }

    private sealed class Fixture : IDisposable
    {
        public IHost Host { get; }
        public HttpClient Client { get; }
        public EventProvider Events { get; } = new();
        public DependencyTransport Transport { get; }
        public DependencyTransport Other { get; } = new(false);
        public ExchangeTransport Exchange { get; }
        private readonly string token = Guid.NewGuid().ToString("N");
        private readonly string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public Fixture(bool typed, bool recovered, bool beforeAuth = false, bool repeat = false, bool missingCredentials = false)
        {
            Transport = new(recovered);
            Exchange = new(token, secret);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
            builder.Configuration["ServiceAuthentication:ClientId"] = "legacy-quotation";
            builder.Configuration["ServiceAuthentication:ClientSecret"] = missingCredentials ? string.Empty : secret;
            builder.Configuration["Services:Auth:BaseUrl"] = "https://auth.example.invalid";
            builder.Logging.AddProvider(Events);
            builder.AddServiceDefaults();
            builder.AddLegacyAuthServiceTokenExchange();
            // Fixture-only supported options: keep actual registered standard policy and unsafe-method predicate.
            builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
                options.Retry.MaxRetryAttempts = 2;
            });
            builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Exchange);
            IHttpClientBuilder client = typed
                ? builder.Services.AddHttpClient<WireClient>("Observed", value => value.BaseAddress = new Uri("https://dependency.example.invalid"))
                : builder.Services.AddHttpClient("Observed", value => value.BaseAddress = new Uri("https://dependency.example.invalid"));
            client.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (beforeAuth) SelectObservation(client);
            client.AddLegacyServiceAuthentication();
            if (!beforeAuth) SelectObservation(client);
            if (repeat) SelectObservation(client);
            builder.Services.AddHttpClient("Unselected", value => value.BaseAddress = new Uri("https://other.example.invalid"))
                .ConfigurePrimaryHttpMessageHandler(() => Other);
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<WireClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Observed");
        }

        public void AssertWireAndPrivacy()
        {
            Assert.All(Transport.Bearers, bearer => Assert.Equal("Bearer " + token, bearer));
            Assert.Equal("legacy-quotation", Exchange.ClientId);
            Assert.Equal(secret, Exchange.ClientSecret);
            AssertPrivacy();
        }

        public void AssertPrivacy()
        {
            foreach (var failure in Events.Failures)
            {
                Assert.Null(failure.Exception);
                Assert.Equal(LogLevel.Error, failure.Level);
                Assert.Equal("ControlledDependency", failure.Fields["Dependency"]);
                Assert.Equal("HttpRequest", failure.Fields["Operation"]);
                Assert.Equal(503, failure.Fields["StatusCode"]);
                Assert.DoesNotContain("private-", failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
                Assert.All(failure.Fields.Values, value => Assert.DoesNotContain("private-", value?.ToString() ?? string.Empty, StringComparison.Ordinal));
                var entry = new LogEntry<IReadOnlyDictionary<string, object?>>(failure.Level, "Controlled.Dependency",
                    new EventId(5101), failure.Fields, failure.Exception, (_, _) => failure.Message);
                using var writer = new StringWriter();
                new PrivateFailureConsoleFormatter().Write(entry, null, writer);
                Assert.DoesNotContain("private-", writer.ToString(), StringComparison.Ordinal);
            }
        }

        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }

    public sealed class WireClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class DependencyTransport(bool recovered) : HttpMessageHandler
    {
        public bool ThrowTransportFailure { get; set; }
        public HttpRequestException OriginalFailure { get; } = new("private-transport-exception-sentinel");
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public List<string?> Bearers { get; } = [];
        public HttpResponseMessage? LastResponse { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Bearers.Add(request.Headers.Authorization?.ToString());
            Method = request.Method.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (ThrowTransportFailure) throw OriginalFailure;
            LastResponse = new(recovered && Calls == 3 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("private-response-sentinel"),
                RequestMessage = request
            };
            return LastResponse;
        }
    }

    private sealed class ExchangeTransport(string token, string secret) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? ClientId { get; private set; }
        public string? ClientSecret { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://auth.example.invalid/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            ClientId = json.RootElement.GetProperty("clientId").GetString();
            ClientSecret = json.RootElement.GetProperty("clientSecret").GetString();
            Assert.Equal(secret, ClientSecret);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = token, expiresIn = 900 })) };
        }
    }

    private sealed record Failure(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);
    private sealed class EventProvider : ILoggerProvider
    {
        public bool ThrowOnFailure { get; set; }
        public ConcurrentQueue<Failure> Failures { get; } = new();
        public ILogger CreateLogger(string categoryName) => new EventLogger(this);
        public void Dispose() { }
        private sealed class EventLogger(EventProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                var values = fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
                if (values.GetValueOrDefault("EventName") as string == "DependencyRequestFailure")
                {
                    provider.Failures.Enqueue(new(level, formatter(state, exception), exception, values));
                    if (provider.ThrowOnFailure) throw new HttpRequestException("Controlled diagnostic provider failed");
                }
            }
        }
    }
}
