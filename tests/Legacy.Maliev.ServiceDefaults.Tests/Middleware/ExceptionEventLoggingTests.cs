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
        Assert.Equal("/orders", entry.Values["Path"]);
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
