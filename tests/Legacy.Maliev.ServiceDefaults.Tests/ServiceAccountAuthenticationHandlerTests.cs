using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Security tests for service-account request authentication logging.</summary>
public sealed class ServiceAccountAuthenticationHandlerTests
{
    /// <summary>Verifies query-string values are never copied into authentication logs.</summary>
    [Fact]
    public async Task SendAsync_RedactsQueryStringFromRequestLogs()
    {
        var logger = new RecordingLogger<ServiceAccountAuthenticationHandler>();
        var handler = new ServiceAccountAuthenticationHandler(
            new StubTokenProvider(),
            logger)
        {
            InnerHandler = new StubHandler()
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync(
            "https://customer.test/search?email=customer@example.test&token=do-not-log");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("customer@example.test", message, StringComparison.Ordinal);
            Assert.DoesNotContain("do-not-log", message, StringComparison.Ordinal);
            Assert.DoesNotContain("?email=", message, StringComparison.Ordinal);
        });
        Assert.Contains(logger.Messages, message => message.Contains("/search", StringComparison.Ordinal));
    }

    private sealed class StubTokenProvider : IServiceAccountTokenProvider
    {
        public string GetToken() => "test-token";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
