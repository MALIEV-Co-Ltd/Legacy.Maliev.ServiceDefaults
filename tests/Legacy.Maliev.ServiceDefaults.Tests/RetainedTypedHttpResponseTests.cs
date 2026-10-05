using System.Net;
using System.Reflection;
using System.Text;
using Maliev.Aspire.ServiceDefaults.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class RetainedTypedHttpResponseTests
{
    [Fact]
    public void SourceResponseModelRetainsGenericClrNameMutablePropertiesAndDefaults()
    {
        var definition = RetainedType("Maliev.Service.WebApi.Model.ApiResponse`1");
        Assert.False(definition.IsSealed);
        var type = definition.MakeGenericType(typeof(List<Currency>));
        Assert.Single(type.GetConstructors());
        Assert.Empty(type.GetConstructors()[0].GetParameters());
        var properties = type.GetProperties();
        Assert.Equal(new[] { "Item", "Response" }, properties.Select(property => property.Name));
        Assert.Equal(typeof(List<Currency>), properties[0].PropertyType);
        Assert.Equal(typeof(HttpResponseMessage), properties[1].PropertyType);
        Assert.All(properties, property => Assert.True(property.CanRead && property.CanWrite));
        var model = Activator.CreateInstance(type)!;
        Assert.Null(Read(model, "Item"));
        Assert.Null(Read(model, "Response"));
        var items = new List<Currency> { new() { Code = "THB" } };
        using var response = new HttpResponseMessage(HttpStatusCode.Created);
        properties[0].SetValue(model, items);
        properties[1].SetValue(model, response);
        Assert.Same(items, Read(model, "Item"));
        Assert.Same(response, Read(model, "Response"));
    }

    [Theory]
    [InlineData("GET", 200)]
    [InlineData("POST", 201)]
    public async Task IntranetCurrencyConsumerReadsRealHttpSuccessThroughOriginalFormatter(string method, int status)
    {
        await using var server = await Server(status, "[{\"code\":\"THB\",\"name\":\"บาท\"}]");
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/currencies/");
        using var response = await client.SendAsync(request);
        var model = await Parse<List<Currency>>(response);
        Assert.Same(response, Read(model, "Response"));
        Assert.Equal(status, (int)response.StatusCode);
        var currency = Assert.Single(Assert.IsType<List<Currency>>(Read(model, "Item")));
        Assert.Equal("THB", currency.Code);
        Assert.Equal("บาท", currency.Name);
        Assert.Equal("/currencies/THB", response.Headers.Location?.OriginalString);
        Assert.Contains("THB", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(304)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task NonSuccessPreservesExactStatusHeadersAndUnparsedBodyForCaller(int status)
    {
        await using var server = await Server(status, "malformed private failure body");
        using var client = server.GetTestClient();
        using var response = await client.GetAsync("/currencies/");
        var model = await Parse<List<Currency>>(response);
        Assert.Same(response, Read(model, "Response"));
        Assert.Null(Read(model, "Item"));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("/currencies/THB", response.Headers.Location?.OriginalString);
        Assert.Equal("malformed private failure body", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonSuccessDoesNotReadOrDisposeContentAndRetainsValueTypeDefault()
    {
        using var content = new ObservedContent("not JSON");
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content };
        var model = await Parse<int>(response);
        Assert.Equal(0, Read(model, "Item"));
        Assert.Same(response, Read(model, "Response"));
        Assert.Equal(0, content.Reads);
        Assert.False(content.Disposed);
        Assert.Equal("not JSON", await content.ReadAsStringAsync());
        Assert.Equal(1, content.Reads);
    }

    [Theory]
    [InlineData("null", 200)]
    [InlineData("", 204)]
    public async Task SuccessNullAndEmptyRetainFormatterDefaultRatherThanInventingItems(string body, int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var model = await Parse<List<Currency>>(response);
        Assert.Null(Read(model, "Item"));
        Assert.Same(response, Read(model, "Response"));
    }

    [Fact]
    public async Task MalformedSuccessPropagatesFormatterFailureAndLeavesResponseOwnedByCaller()
    {
        using var content = new ObservedContent("this is not JSON");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Parse<List<Currency>>(response));
        Assert.Equal("Newtonsoft.Json.JsonReaderException", error.GetType().FullName);
        Assert.False(content.Disposed);
        Assert.Equal("this is not JSON", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuccessfulReadDoesNotDisposeTheCallerResponseOrContent()
    {
        using var content = new ObservedContent("{\"code\":\"THB\"}");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var model = await Parse<Currency>(response);
        Assert.Equal("THB", Assert.IsType<Currency>(Read(model, "Item")).Code);
        Assert.False(content.Disposed);
        response.Dispose();
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task NullResponseFailsExplicitlyRatherThanReturningAnInventedResult()
    {
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => Parse<Currency>(null!));
        Assert.Equal("response", error.ParamName);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    public async Task AlreadyCancelledReadPropagatesCallerCancellationWithoutReadingOrDisposing(int status)
    {
        using var content = new ObservedContent("{\"code\":\"THB\"}");
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Parse<Currency>(response, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, content.Reads);
        Assert.False(content.Disposed);
    }

    private static Type RetainedType(string name)
    {
        var type = typeof(PrivateStartupBoundary).Assembly.GetType(name);
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        return type;
    }

    private static object? Read(object model, string property) => model.GetType().GetProperty(property)!.GetValue(model);

    private static async Task<object> Parse<T>(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var type = RetainedType("Maliev.Service.WebApi.WebApiResponseReader");
        var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static));
        Assert.Equal("ReadAsAsync", method.Name);
        Assert.Equal(new[] { typeof(HttpResponseMessage), typeof(CancellationToken) }, method.GetParameters().Select(parameter => parameter.ParameterType));
        Task task;
        try
        {
            task = Assert.IsAssignableFrom<Task>(method.MakeGenericMethod(typeof(T)).Invoke(null, [response, cancellationToken]));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        await task;
        return Assert.IsAssignableFrom<object>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private static async Task<WebApplication> Server(int status, string body)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Location = "/currencies/THB";
            await context.Response.WriteAsync(body);
        });
        await app.StartAsync();
        return app;
    }

    public sealed class Currency
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ObservedContent : HttpContent
    {
        private readonly byte[] bytes;
        public ObservedContent(string body)
        {
            bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new("application/json");
        }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            await stream.WriteAsync(bytes);
        }
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
