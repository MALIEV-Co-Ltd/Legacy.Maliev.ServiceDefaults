using Maliev.Aspire.ServiceDefaults.Caching;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class ClientAndRedisRegistrationBehaviorTests
{
    [Fact]
    public void NamedServiceClient_UsesOverrideThenConfigurationThenDiscovery()
    {
        using var configured = BuildClientHost(builder =>
        {
            builder.Configuration["Services:Configured:BaseUrl"] = "https://configured.example.test";
            builder.AddServiceClient("Configured");
            builder.AddServiceClient("Overridden", "https://override.example.test", client => client.DefaultRequestHeaders.Add("X-Mode", "review"));
            builder.AddServiceClient("Discovered");
        });
        var factory = configured.Services.GetRequiredService<IHttpClientFactory>();
        using HttpClient fromConfiguration = factory.CreateClient("Configured");
        using HttpClient overridden = factory.CreateClient("Overridden");
        using HttpClient discovered = factory.CreateClient("Discovered");
        Assert.Equal("https://configured.example.test/", fromConfiguration.BaseAddress!.AbsoluteUri);
        Assert.Equal("https://override.example.test/", overridden.BaseAddress!.AbsoluteUri);
        Assert.Equal("review", Assert.Single(overridden.DefaultRequestHeaders.GetValues("X-Mode")));
        Assert.Equal("https+http://discovered/", discovered.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void TypedAndConvenienceServiceClientsExposeExpectedNames()
    {
        using var host = BuildClientHost(builder =>
        {
            builder.AddServiceClient<IReviewClient, ReviewClient>("Typed");
            builder.AddUploadServiceClient();
            builder.AddPdfServiceClient();
            builder.AddNotificationServiceClient();
            builder.AddCustomerServiceClient();
        });
        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        Assert.Equal("https+http://typed/", Assert.IsType<ReviewClient>(host.Services.GetRequiredService<IReviewClient>()).Client.BaseAddress!.AbsoluteUri);
        Assert.Equal("https+http://uploadservice/", factory.CreateClient("UploadService").BaseAddress!.AbsoluteUri);
        Assert.Equal("https+http://pdfservice/", factory.CreateClient("PdfService").BaseAddress!.AbsoluteUri);
        Assert.Equal("https+http://notificationservice/", factory.CreateClient("NotificationService").BaseAddress!.AbsoluteUri);
        Assert.Equal("https+http://customerservice/", factory.CreateClient("CustomerService").BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void RedisDisabledRegistersNoCacheService()
    {
        var builder = CreateBuilder("Testing");
        builder.Configuration["Redis:Enabled"] = "false";
        builder.AddRedisDistributedCache();
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ICacheService));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
    }

    [Fact]
    public void RedisProductionWithoutConnectionFailsAndDoesNotExposeOtherValues()
    {
        var builder = CreateBuilder(Environments.Production);
        builder.Configuration["ConnectionStrings:Other"] = "private-value";
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddRedisDistributedCache());
        Assert.Contains("Other", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedisTestingRegistersConfiguredOptionsWithoutOpeningConnection()
    {
        var builder = CreateBuilder("Testing");
        builder.Configuration["Redis:ConnectTimeout"] = "1234";
        builder.Configuration["Redis:SyncTimeout"] = "2345";
        builder.Configuration["Redis:AsyncTimeout"] = "3456";
        builder.AddRedisDistributedCache("review:", options => options.ConnectRetry = 4);
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(ICacheService));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IDistributedCache));
        // The multiplexer factory remains lazy: registration validation must not connect to Redis.
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
    }

    [Fact]
    public void RedisMultiplexerRequiresNamedConnectionWithoutConnectingDuringRegistration()
    {
        var missing = CreateBuilder("Testing");
        Assert.Throws<InvalidOperationException>(() => missing.AddRedisConnectionMultiplexer("cache"));

        var configured = CreateBuilder("Testing");
        configured.Configuration["ConnectionStrings:cache"] = "localhost:6379";
        configured.AddRedisConnectionMultiplexer("cache", options => options.ConnectRetry = 3);
        Assert.Contains(configured.Services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
    }

    private static IHost BuildClientHost(Action<HostApplicationBuilder> configure)
    {
        var builder = CreateBuilder("Testing");
        configure(builder);
        return builder.Build();
    }

    private static HostApplicationBuilder CreateBuilder(string environment)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        return builder;
    }

    public interface IReviewClient;

    public sealed class ReviewClient(HttpClient client) : IReviewClient
    {
        public HttpClient Client { get; } = client;
    }
}
