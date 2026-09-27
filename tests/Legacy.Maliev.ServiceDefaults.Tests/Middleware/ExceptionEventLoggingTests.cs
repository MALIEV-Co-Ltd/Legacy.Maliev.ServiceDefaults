using System.Globalization;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests.Middleware;

public sealed class ExceptionEventLoggingTests
{
    [Fact]
    public async Task StartedResponse_PreservesStatusAndBody_WithoutLoggingExceptionSecrets()
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        var response = new StartedResponseFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(response);
        var middleware = new ExceptionHandlingMiddleware(_ => throw new Exception("private-message"), logger, new TestEnvironment());

        await middleware.InvokeAsync(context);

        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(0, response.Body.Length);
        Assert.Equal(202, logger.Entries[0].Values["StatusCode"]);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("private-message", entry.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(true, 400)]
    [InlineData(false, 500)]
    public async Task Failure_EmitsCorrelatedNativeEventWithoutSecrets_AndPreservesJson(bool validation, int status)
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "request-correlation";
        context.Request.Method = "POST";
        context.Request.Path = "/orders";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Request.Headers.Authorization = "Bearer private-header";
        Exception failure = validation ? new ArgumentException("private-message") : new Exception("private-message");
        var middleware = new ExceptionHandlingMiddleware(_ => throw failure, logger, new TestEnvironment());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(8, entry.Values.Count);
        Assert.Equal("UnhandledRequestFailure", entry.Values["EventName"]);
        Assert.Equal("Legacy.Maliev.OrderService", entry.Values["Service"]);
        Assert.Equal("POST", entry.Values["Method"]);
        Assert.Equal("/", entry.Values["Path"]);
        Assert.Equal(status, entry.Values["StatusCode"]);
        Assert.Equal(failure.GetType().Name, entry.Values["ExceptionType"]);
        Assert.Equal(context.TraceIdentifier, entry.Values["IncidentId"]);
        var timestamp = Assert.IsType<string>(entry.Values["OccurredAtUtc"]);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture).Offset);
        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(status, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(context.TraceIdentifier, json.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("private-", json.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotFoundException_EmitsDebugEventWithoutSecrets_AndPreserves404(bool domainException)
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "request-correlation";
        context.Request.Method = "GET";
        context.Request.Path = "/orders/private-email@example.test";
        context.Request.QueryString = new QueryString("?token=private-query");
        context.Request.Headers.Authorization = "Bearer private-header";
        Exception failure = domainException
            ? new SampleNotFoundException("private-message")
            : new KeyNotFoundException("private-message");
        var middleware = new ExceptionHandlingMiddleware(_ => throw failure, logger, new TestEnvironment());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("UnhandledRequestFailure", entry.Values["EventName"]);
        Assert.Equal(404, entry.Values["StatusCode"]);
        Assert.Equal("/", entry.Values["Path"]);
        Assert.Equal(context.TraceIdentifier, entry.Values["IncidentId"]);
        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
        Assert.Equal(404, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(404, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(context.TraceIdentifier, json.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("private-", json.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchedFailure_LogsRouteTemplateWithoutLiteralIdentifier()
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/orders/private-customer-id";
        context.SetEndpoint(new Microsoft.AspNetCore.Routing.RouteEndpointBuilder(
            _ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/orders/{id}"),
            0).Build());
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("private-message"), logger, new TestEnvironment());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("/orders/{id}", entry.Values["Path"]);
        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task DirectNotFoundResponse_DoesNotEmitExceptionEvent()
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        var middleware = new ExceptionHandlingMiddleware(
            current =>
            {
                current.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            logger,
            new TestEnvironment());

        await middleware.InvokeAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Empty(logger.Entries);
    }

    private sealed class SampleNotFoundException(string message) : Exception(message);

    private sealed class CaptureLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(level, exception, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).Where(pair => pair.Key != "{OriginalFormat}").ToDictionary()));
    }

    private sealed record Entry(LogLevel Level, Exception? Exception, string Message, Dictionary<string, object?> Values);

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 202;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Legacy.Maliev.OrderService";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
