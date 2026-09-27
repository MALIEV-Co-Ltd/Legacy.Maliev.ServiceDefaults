using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests.Middleware;

public sealed class RequestLoggingPathTests
{
    [Theory]
    [InlineData(true, "/orders/{id}")]
    [InlineData(false, "/")]
    public async Task RequestLogs_UseRouteTemplateWithoutLiteralSegments(bool matched, string expectedPath)
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/orders/private-customer-id";
        context.Request.QueryString = new QueryString("?token=private-query");
        if (matched)
        {
            context.SetEndpoint(new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/orders/{id}"),
                0).Build());
        }

        var middleware = new RequestLoggingMiddleware(current =>
        {
            current.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(expectedPath, entry.Values["Path"]);
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);
        });
        Assert.Equal(StatusCodes.Status204NoContent, logger.Entries[1].Values["StatusCode"]);
    }

    [Fact]
    public async Task EndpointSelectedDownstream_CompletionUsesTemplateAndStartUsesFallback()
    {
        var logger = new CaptureLogger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/orders/private-customer-id";
        var middleware = new RequestLoggingMiddleware(current =>
        {
            current.SetEndpoint(new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/orders/{id}"),
                0).Build());
            return Task.CompletedTask;
        }, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal("/", logger.Entries[0].Values["Path"]);
        Assert.Equal("/orders/{id}", logger.Entries[1].Values["Path"]);
        Assert.All(logger.Entries, entry =>
            Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal));
    }

    private sealed class CaptureLogger : ILogger<RequestLoggingMiddleware>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).Where(pair => pair.Key != "{OriginalFormat}").ToDictionary()));
    }

    private sealed record Entry(string Message, Dictionary<string, object?> Values);
}
