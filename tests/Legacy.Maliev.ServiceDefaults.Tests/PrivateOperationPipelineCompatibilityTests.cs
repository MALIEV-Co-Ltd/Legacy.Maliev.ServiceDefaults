using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Producer integration controls only: these do not activate or accept any consumer.
// The factory, Defaults resilience policy and LegacyAuth exchange/handler are real;
// only outbound primary transports and supported fixture timing options are controlled.
public sealed class PrivateOperationPipelineCompatibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OperationMode_RetriedRead_ObservesOnlyTerminalOutcome(bool typed, bool recovered)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Mode = recovered ? Outcome.Recover : Outcome.Unavailable;

        using var response = await fixture.Client.GetAsync("/orders/private-path-sentinel?token=private-query-sentinel");

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(recovered ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, fixture.Transport.Calls);
        fixture.AssertAuthenticated(1);
        if (recovered) Assert.Empty(fixture.Events.Failures);
        else fixture.AssertFailure("Orders.Get", 503);
    }

    [Theory]
    [InlineData(false, "POST", "Customers.Post")]
    [InlineData(true, "POST", "Customers.Post")]
    [InlineData(false, "PUT", "Customers.Put")]
    [InlineData(true, "PUT", "Customers.Put")]
    public async Task OperationMode_UnsafeWrite_PreservesBodyResponseAndNoRetry(bool typed, string method, string operation)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Mode = Outcome.Unavailable;
        using var request = new HttpRequestMessage(new HttpMethod(method), "/customers/private-path-sentinel")
        { Content = new StringContent("private-body-sentinel") };
        request.Headers.Add("X-Fixture-Private", "private-header-sentinel");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Equal(method, fixture.Transport.Method);
        Assert.Equal("private-body-sentinel", fixture.Transport.Body);
        Assert.Equal("private-header-sentinel", fixture.Transport.PrivateHeader);
        fixture.AssertAuthenticated(1);
        fixture.AssertFailure(operation, 503);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationMode_RealAttemptTimeout_EmitsOneTerminalEventWithoutInventedStatus(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Mode = Outcome.Block;

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => fixture.Client.GetAsync("/orders/private-timeout-sentinel"));

        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(3, fixture.Transport.Cancellations);
        fixture.AssertAuthenticated(1);
        fixture.AssertFailure("Orders.Get", null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationMode_InFlightCallerCancellation_IsQuietAndNotRetried(bool typed)
    {
        using var fixture = new Fixture(typed);
        using var warm = await fixture.Client.GetAsync("/orders/warm");
        fixture.Transport.Mode = Outcome.Block;
        using var cancellation = new CancellationTokenSource();
        var send = fixture.Client.GetAsync("/orders/private-cancel-sentinel", cancellation.Token);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Equal(2, fixture.Transport.Calls);
        Assert.Equal(1, fixture.Transport.Cancellations);
        fixture.AssertAuthenticated(1);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationMode_MissingCredentials_PreservesRealAuthFailureAndRequestOperation(bool typed)
    {
        using var fixture = new Fixture(typed, missingCredentials: true);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.PostAsync("/customers/private-path-sentinel", new StringContent("private-body-sentinel")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Equal(0, fixture.Exchange.Calls);
        fixture.AssertFailure("Customers.Post", 503);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    public async Task OperationMode_RejectedBearer_PreservesResponseAndRealTokenInvalidation(bool typed, int status)
    {
        using var fixture = new Fixture(typed);
        using var warm = await fixture.Client.GetAsync("/orders/warm");
        fixture.Transport.Mode = status == 401 ? Outcome.Unauthorized : Outcome.Forbidden;

        using var rejected = await fixture.Client.PostAsync("/customers/private-path-sentinel", new StringContent("bounded"));

        Assert.Same(fixture.Transport.LastResponse, rejected);
        Assert.Equal((HttpStatusCode)status, rejected.StatusCode);
        fixture.Transport.Mode = Outcome.Ok;
        using var refreshed = await fixture.Client.GetAsync("/orders/after-rejection");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(2, fixture.Exchange.Calls);
        Assert.Equal(new[] { "Bearer " + fixture.FirstToken, "Bearer " + fixture.FirstToken, "Bearer " + fixture.SecondToken },
            fixture.Transport.Bearers);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OperationMode_RepeatedSelectionBeforeOrAfterAuth_KeepsSingleOuterObserver(bool typed, bool beforeAuth)
    {
        using var fixture = new Fixture(typed, beforeAuth: beforeAuth, repeat: true);
        fixture.Transport.Mode = Outcome.Unavailable;

        using var response = await fixture.Client.PostAsync("/customers/private-path-sentinel", new StringContent("bounded"));

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(1, fixture.Transport.Calls);
        fixture.AssertPipeline();
        fixture.AssertAuthenticated(1);
        fixture.AssertFailure("Customers.Post", 503);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OperationMode_ThrowingProvider_PreservesOriginalResponseOrTransportException(bool typed, bool transportFailure)
    {
        using var fixture = new Fixture(typed);
        fixture.Events.ThrowOnFailure = true;
        fixture.Transport.Mode = transportFailure ? Outcome.Throw : Outcome.Unavailable;

        if (transportFailure)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
                fixture.Client.PostAsync("/customers/private-path-sentinel", new StringContent("bounded")));
            Assert.Same(fixture.Transport.OriginalFailure, failure);
            Assert.Null(failure.StatusCode);
        }
        else
        {
            using var response = await fixture.Client.PostAsync("/customers/private-path-sentinel", new StringContent("bounded"));
            Assert.Same(fixture.Transport.LastResponse, response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("private-response-sentinel", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, fixture.Transport.Calls);
        fixture.AssertAuthenticated(1);
        fixture.AssertFailure("Customers.Post", transportFailure ? null : 503);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationMode_UnselectedClient_RemainsUnobserved(bool typed)
    {
        using var fixture = new Fixture(typed);
        using var other = fixture.Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("OperationUnselected");

        using var response = await other.PostAsync("/customers/private-path-sentinel", new StringContent("bounded"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, fixture.Other.Calls);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Equal(0, fixture.Exchange.Calls);
        Assert.Empty(fixture.Events.Failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OperationMode_SelectionBeforeOrAfterDefaults_RemainsOutsideRealAuthAndResilience(bool typed, bool beforeDefaults)
    {
        using var fixture = new Fixture(typed, beforeDefaults: beforeDefaults);
        fixture.Transport.Mode = Outcome.Unavailable;

        using var response = await fixture.Client.GetAsync("/orders/private-path-sentinel");

        Assert.Same(fixture.Transport.LastResponse, response);
        Assert.Equal(3, fixture.Transport.Calls);
        fixture.AssertPipeline();
        fixture.AssertAuthenticated(1);
        fixture.AssertFailure("Orders.Get", 503);
    }

    public sealed class OperationPipelineClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    private enum Outcome { Ok, Unavailable, Recover, Block, Throw, Unauthorized, Forbidden }

    private sealed class Fixture : IDisposable
    {
        public string FirstToken { get; } = Guid.NewGuid().ToString("N");
        public string SecondToken { get; } = Guid.NewGuid().ToString("N");
        private readonly string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public Transport Transport { get; } = new();
        public Transport Other { get; } = new() { Mode = Outcome.Unavailable };
        public Events Events { get; } = new();
        public LoginTransport Exchange { get; }
        public IHost Host { get; }
        public HttpClient Client { get; }

        public Fixture(bool typed, bool beforeDefaults = false, bool beforeAuth = false,
            bool repeat = false, bool missingCredentials = false)
        {
            Exchange = new(FirstToken, SecondToken, secret);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
            builder.Configuration["Observability:RuntimeMetricsEnabled"] = "false";
            builder.Configuration["Services:Auth:BaseUrl"] = "https://auth.example.invalid";
            builder.Configuration["ServiceAuthentication:ClientId"] = "legacy-quotation";
            builder.Configuration["ServiceAuthentication:ClientSecret"] = missingCredentials ? string.Empty : secret;
            builder.Logging.AddProvider(Events);
            if (!beforeDefaults) builder.AddServiceDefaults();
            builder.AddLegacyAuthServiceTokenExchange();
            builder.Services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Exchange);
            var registration = typed
                ? builder.Services.AddHttpClient<OperationPipelineClient>("OperationPipelineProof",
                    value => value.BaseAddress = new Uri("https://dependency.example.invalid"))
                : builder.Services.AddHttpClient("OperationPipelineProof",
                    value => value.BaseAddress = new Uri("https://dependency.example.invalid"));
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (beforeAuth) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            registration.AddLegacyServiceAuthentication();
            if (!beforeAuth) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            if (repeat) registration.AddPrivateFailureOperationObservation("ControlledDependency");
            builder.Services.AddHttpClient("OperationUnselected", value => value.BaseAddress = new Uri("https://other.example.invalid"))
                .ConfigurePrimaryHttpMessageHandler(() => Other);
            if (beforeDefaults) builder.AddServiceDefaults();
            // Keep production retry predicates/strategies; shorten only supported fixture timing options.
            builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.UseJitter = false;
                options.Retry.MaxRetryAttempts = 2;
            });
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<OperationPipelineClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("OperationPipelineProof");
        }

        public void AssertPipeline()
        {
            HttpMessageHandler current = Host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("OperationPipelineProof");
            var chain = new List<string>();
            while (current is DelegatingHandler handler)
            {
                chain.Add(current.GetType().Name);
                current = Assert.IsAssignableFrom<HttpMessageHandler>(handler.InnerHandler);
            }
            Assert.Single(chain, name => name == "PrivateDependencyFailureHandler");
            Assert.Single(chain, name => name == nameof(LegacyServiceAuthenticationHandler));
            Assert.Contains("ResilienceHandler", chain);
            int observer = chain.IndexOf("PrivateDependencyFailureHandler");
            Assert.True(observer < chain.IndexOf(nameof(LegacyServiceAuthenticationHandler)));
            Assert.True(observer < chain.IndexOf("ResilienceHandler"));
            Assert.Same(Transport, current);
        }

        public void AssertAuthenticated(int exchangeCalls)
        {
            Assert.Equal(exchangeCalls, Exchange.Calls);
            Assert.All(Transport.Bearers, bearer => Assert.Equal("Bearer " + FirstToken, bearer));
        }

        public void AssertFailure(string operation, int? status)
        {
            var failure = Assert.Single(Events.Failures);
            Assert.Equal(LogLevel.Error, failure.Level);
            Assert.Equal(5101, failure.Id.Id);
            Assert.Equal("DependencyRequestFailure", failure.Id.Name);
            Assert.Null(failure.Exception);
            Assert.Equal("DependencyRequestFailure", failure.Fields["EventName"]);
            Assert.Equal("ControlledDependency", failure.Fields["Dependency"]);
            Assert.Equal(operation, failure.Fields["Operation"]);
            if (status is { } known) Assert.Equal(known, failure.Fields["StatusCode"]);
            else Assert.False(failure.Fields.ContainsKey("StatusCode"));
            var entry = new LogEntry<IReadOnlyDictionary<string, object?>>(failure.Level, failure.Category,
                failure.Id, failure.Fields, failure.Exception, (_, _) => failure.Message);
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(entry, null, writer);
            string wire = writer.ToString();
            foreach (var protectedValue in new[] { "private-", FirstToken, SecondToken, secret,
                "dependency.example.invalid", "auth.example.invalid" })
            {
                Assert.DoesNotContain(protectedValue, failure.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(protectedValue, JsonSerializer.Serialize(failure.Fields), StringComparison.Ordinal);
                Assert.DoesNotContain(protectedValue, wire, StringComparison.Ordinal);
            }
            using var document = JsonDocument.Parse(wire);
            Assert.Equal(operation, document.RootElement.GetProperty("Operation").GetString());
            Assert.Equal("ERROR", document.RootElement.GetProperty("severity").GetString());
            if (status is { } number) Assert.Equal(number, document.RootElement.GetProperty("StatusCode").GetInt32());
            else Assert.False(document.RootElement.TryGetProperty("StatusCode", out _));
        }

        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public Outcome Mode { get; set; }
        public int Calls { get; private set; }
        public int Cancellations { get; private set; }
        public List<string?> Bearers { get; } = [];
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        public string? PrivateHeader { get; private set; }
        public HttpResponseMessage? LastResponse { get; private set; }
        public HttpRequestException OriginalFailure { get; } = new("private-transport-sentinel");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Bearers.Add(request.Headers.Authorization?.ToString());
            Method = request.Method.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            PrivateHeader = request.Headers.TryGetValues("X-Fixture-Private", out var header) ? Assert.Single(header) : null;
            if (Mode == Outcome.Throw) throw OriginalFailure;
            if (Mode == Outcome.Block)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancellations++; throw; }
            }
            LastResponse = new(Mode switch
            {
                Outcome.Unavailable => HttpStatusCode.ServiceUnavailable,
                Outcome.Recover => Calls == 3 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                Outcome.Unauthorized => HttpStatusCode.Unauthorized,
                Outcome.Forbidden => HttpStatusCode.Forbidden,
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
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal("legacy-quotation", body.RootElement.GetProperty("clientId").GetString());
            Assert.Equal(secret, body.RootElement.GetProperty("clientSecret").GetString());
            return new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = Calls == 1 ? first : second, expiresIn = 900 })) };
        }
    }

    private sealed record Failure(LogLevel Level, string Category, EventId Id, string Message,
        Exception? Exception, IReadOnlyDictionary<string, object?> Fields);

    private sealed class Events : ILoggerProvider
    {
        public bool ThrowOnFailure { get; set; }
        public ConcurrentQueue<Failure> Failures { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void Dispose() { }
        private sealed class Capture(Events provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level != LogLevel.None;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (id.Id != 5101 || id.Name != "DependencyRequestFailure") return;
                var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                    .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
                provider.Failures.Enqueue(new(level, category, id, formatter(state, exception), exception, values));
                if (provider.ThrowOnFailure) throw new HttpRequestException("Controlled diagnostic provider failed");
            }
        }
    }
}
