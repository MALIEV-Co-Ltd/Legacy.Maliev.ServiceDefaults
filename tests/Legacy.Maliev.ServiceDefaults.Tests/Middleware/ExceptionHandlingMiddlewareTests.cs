using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Regression tests for the shared exception boundary.</summary>
public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ProductionArgumentException_UsesSanitizedClientMessage()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new ArgumentException("connection=Host=private-db;Password=secret"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new TestHostEnvironment(Environments.Production));
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        var body = await ReadBodyAsync(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("The request is invalid.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-db", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ClientCancellation_RethrowsWithoutWritingErrorResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(cancellation.Token),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new TestHostEnvironment(Environments.Production));
        var context = CreateContext();
        context.RequestAborted = cancellation.Token;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = typeof(ExceptionHandlingMiddlewareTests).Assembly.GetName().Name!;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
