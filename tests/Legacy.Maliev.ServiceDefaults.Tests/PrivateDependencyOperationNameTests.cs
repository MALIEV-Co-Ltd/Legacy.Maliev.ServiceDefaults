using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Source 38a6247161979d046354401ec32e1fb5d7546f53 operation acceptance.
// The generic registration remains separately frozen to HttpRequest.
public sealed class PrivateDependencyOperationNameTests
{
    [Theory]
    [InlineData(false, "auth", "Auth.Get")]
    [InlineData(true, "countries", "Countries.Get")]
    [InlineData(false, "currencies", "Currencies.Get")]
    [InlineData(true, "customers", "Customers.Get")]
    [InlineData(false, "emails", "Emails.Get")]
    [InlineData(true, "employees", "Employees.Get")]
    [InlineData(false, "invoices", "Invoices.Get")]
    [InlineData(true, "jobs", "Jobs.Get")]
    [InlineData(false, "materials", "Materials.Get")]
    [InlineData(true, "messages", "Messages.Get")]
    [InlineData(false, "orders", "Orders.Get")]
    [InlineData(true, "orderstatuses", "OrderStatuses.Get")]
    [InlineData(false, "payments", "Payments.Get")]
    [InlineData(true, "pdfs", "Pdfs.Get")]
    [InlineData(false, "purchaseorders", "PurchaseOrders.Get")]
    [InlineData(true, "quotationrequests", "QuotationRequests.Get")]
    [InlineData(false, "quotations", "Quotations.Get")]
    [InlineData(true, "receipts", "Receipts.Get")]
    [InlineData(false, "suppliers", "Suppliers.Get")]
    [InlineData(true, "uploads", "Uploads.Get")]
    public async Task Retained_resource_EmitsOnlyBoundedOperation(bool typed, string resource, string expected)
    {
        using var fixture = new Fixture(typed);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            resource + "/private-dynamic-sentinel?credential=private-query-sentinel");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Same(fixture.Transport.Response, response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        fixture.AssertFailure(expected, 503);
    }

    [Theory]
    [InlineData(false, "GET", "Orders.Get")]
    [InlineData(true, "POST", "Orders.Post")]
    [InlineData(false, "PUT", "Orders.Put")]
    [InlineData(true, "PATCH", "Orders.Patch")]
    [InlineData(false, "DELETE", "Orders.Delete")]
    [InlineData(true, "HEAD", "Orders.Head")]
    [InlineData(false, "OPTIONS", "Orders.Options")]
    public async Task Uppercase_verb_and_case_insensitive_first_resource_EmitLiteralOperation(bool typed, string verb, string expected)
    {
        using var fixture = new Fixture(typed);
        using var request = new HttpRequestMessage(new HttpMethod(verb),
            "https://dependency.example.invalid/OrDeRs/private-dynamic-sentinel?secret=private-query-sentinel");

        using var response = await fixture.Client.SendAsync(request);

        fixture.AssertFailure(expected, 503);
    }

    [Theory]
    [InlineData(false, "GET", "/private-resource-sentinel/orders?secret=private-query-sentinel")]
    [InlineData(true, "GET", "/v1/orders/private-dynamic-sentinel")]
    [InlineData(false, "GET", "/predictions/private-dynamic-sentinel")]
    [InlineData(true, "GET", "/")]
    [InlineData(false, "GET", "?orders=private-query-sentinel")]
    [InlineData(true, "TRACE", "/orders/private-dynamic-sentinel")]
    [InlineData(false, "CONNECT", "/customers/private-dynamic-sentinel")]
    [InlineData(true, "GET-private-verb-sentinel", "/orders/private-dynamic-sentinel")]
    public async Task Unknown_or_retired_first_resource_or_verb_EmitsOnlyUnknownOperation(bool typed, string verb, string target)
    {
        using var fixture = new Fixture(typed);
        using var request = new HttpRequestMessage(new HttpMethod(verb), target);

        using var response = await fixture.Client.SendAsync(request);

        fixture.AssertFailure("UnknownOperation", 503);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failure_PreservesOriginalExceptionAndSafeCustomersPost(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Throw = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/customers/private-dynamic-sentinel")
        { Content = new StringContent("private-body-sentinel") };
        request.Headers.Add("X-Private-Fixture", "private-header-sentinel");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.SendAsync(request));

        Assert.Same(fixture.Transport.Failure, failure);
        fixture.AssertFailure("Customers.Post", null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightCallerCancellation_RemainsQuiet(bool typed)
    {
        using var fixture = new Fixture(typed);
        fixture.Transport.Block = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/private-dynamic-sentinel");
        var send = fixture.Client.SendAsync(request, cancellation.Token);
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.Events.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingGenericRegistration_PreservesHttpRequestOperation(bool typed)
    {
        using var fixture = new Fixture(typed,
            registration => registration.AddPrivateFailureObservation("ReviewDependency"));
        using var response = await fixture.Client.GetAsync("/orders/private-dynamic-sentinel");

        Assert.Same(fixture.Transport.Response, response);
        fixture.AssertFailure("HttpRequest", 503);
    }

    public sealed class OperationClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedOperationRegistration_IsIdempotent(bool typed)
    {
        using var fixture = new Fixture(typed, registration =>
        {
            registration.AddPrivateFailureOperationObservation("ReviewDependency");
            registration.AddPrivateFailureOperationObservation("ReviewDependency");
        });
        using var response = await fixture.Client.GetAsync("/orders/private-dynamic-sentinel");
        fixture.AssertFailure("Orders.Get", 503);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedRegistrationModes_FailClosed(bool operationFirst)
    {
        var services = new ServiceCollection();
        var registration = services.AddHttpClient("ModeProof");
        if (operationFirst)
        {
            registration.AddPrivateFailureOperationObservation("ReviewDependency");
            Assert.Throws<InvalidOperationException>(() => registration.AddPrivateFailureObservation("ReviewDependency"));
        }
        else
        {
            registration.AddPrivateFailureObservation("ReviewDependency");
            Assert.Throws<InvalidOperationException>(() => registration.AddPrivateFailureOperationObservation("ReviewDependency"));
        }
    }

    [Fact]
    public void OperationRegistration_ConflictingDependency_FailsClosed()
    {
        var registration = new ServiceCollection().AddHttpClient("IdentityProof");
        registration.AddPrivateFailureOperationObservation("ReviewDependency");
        Assert.Throws<InvalidOperationException>(() => registration.AddPrivateFailureOperationObservation("OtherDependency"));
    }

    [Fact]
    public async Task OversizedUnknownResource_DoesNotExportOrClassifyLaterResource()
    {
        using var fixture = new Fixture(false);
        using var response = await fixture.Client.GetAsync("/" + new string('a', 4096) + "/orders");
        fixture.AssertFailure("UnknownOperation", 503);
    }

    private sealed class Fixture : IDisposable
    {
        public Transport Transport { get; } = new();
        public Events Events { get; } = new();
        public IHost Host { get; }
        public HttpClient Client { get; }

        public Fixture(bool typed, Action<IHttpClientBuilder>? selectObservation = null)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            { DisableDefaults = true });
            builder.Logging.AddProvider(Events);
            var registration = typed
                ? builder.Services.AddHttpClient<OperationClient>("OperationProof", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"))
                : builder.Services.AddHttpClient("OperationProof", client => client.BaseAddress = new Uri("https://dependency.example.invalid/"));
            registration.ConfigurePrimaryHttpMessageHandler(() => Transport);
            if (selectObservation is null)
            {
                registration.AddPrivateFailureOperationObservation("ReviewDependency");
            }
            else
            {
                selectObservation(registration);
            }
            Host = builder.Build();
            Client = typed ? Host.Services.GetRequiredService<OperationClient>().Client
                : Host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("OperationProof");
        }

        public void AssertFailure(string operation, int? status)
        {
            Assert.Equal(1, Transport.Calls);
            var entry = Assert.Single(Events.Entries);
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(5101, entry.Id.Id);
            Assert.Equal("DependencyRequestFailure", entry.Id.Name);
            Assert.Null(entry.Exception);
            Assert.Equal("DependencyRequestFailure", entry.Fields["EventName"]);
            Assert.Equal("ReviewDependency", entry.Fields["Dependency"]);
            if (status is { } known) Assert.Equal(known, entry.Fields["StatusCode"]);
            else Assert.False(entry.Fields.ContainsKey("StatusCode"));
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(entry.Fields), StringComparison.Ordinal);
            var log = new LogEntry<IReadOnlyDictionary<string, object?>>(entry.Level, entry.Category,
                entry.Id, entry.Fields, entry.Exception, (_, _) => entry.Message);
            using var writer = new StringWriter();
            new PrivateFailureConsoleFormatter().Write(log, null, writer);
            var wire = writer.ToString();
            Assert.DoesNotContain("private-", wire, StringComparison.Ordinal);
            Assert.DoesNotContain("dependency.example.invalid", wire, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(wire);
            Assert.Equal("ERROR", json.RootElement.GetProperty("severity").GetString());
            Assert.Equal(5101, json.RootElement.GetProperty("eventId").GetInt32());
            Assert.Equal("ReviewDependency", json.RootElement.GetProperty("Dependency").GetString());
            if (status is { } number) Assert.Equal(number, json.RootElement.GetProperty("StatusCode").GetInt32());
            else Assert.False(json.RootElement.TryGetProperty("StatusCode", out _));
            // Include both actual boundaries in the same expected missing-contract assertion.
            var actualStateOperation = Assert.IsType<string>(entry.Fields["Operation"]);
            var actualWireOperation = json.RootElement.GetProperty("Operation").GetString();
            Assert.Equal(new[] { operation, operation }, new[] { actualStateOperation, actualWireOperation });
        }

        public void Dispose()
        {
            try { Client.Dispose(); }
            finally
            {
                try { Host.Dispose(); }
                finally { Transport.Dispose(); }
            }
        }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpRequestException Failure { get; } = new("private-transport-sentinel");
        public HttpResponseMessage? Response { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw) throw Failure;
            if (Block)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            Response = new(HttpStatusCode.ServiceUnavailable)
            { RequestMessage = request, Content = new StringContent("private-response-sentinel") };
            return Response;
        }
    }

    private sealed record Entry(LogLevel Level, string Category, EventId Id, Exception? Exception,
        string Message, IReadOnlyDictionary<string, object?> Fields);

    private sealed class Events : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 5101 || eventId.Name != "DependencyRequestFailure") return;
                var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                entries.Enqueue(new(logLevel, category, eventId, exception, formatter(state, exception), fields));
            }
        }
    }
}
