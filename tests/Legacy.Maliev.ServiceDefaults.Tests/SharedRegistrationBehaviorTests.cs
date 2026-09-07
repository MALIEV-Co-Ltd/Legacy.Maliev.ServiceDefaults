using Asp.Versioning;
using Maliev.Aspire.ServiceDefaults.Database;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class SharedRegistrationBehaviorTests
{
    [Theory]
    [InlineData("Testing", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", CookieSecurePolicy.Always)]
    public void IdentityCookie_RegistersSharedSchemesAndSecurityContract(string environment, CookieSecurePolicy securePolicy)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration["Auth:CookieDomain"] = ".example.test";
        var callbackObserved = false;

        builder.AddMalievIdentityCookie(options =>
        {
            callbackObserved = true;
            options.Cookie.HttpOnly = false;
        });

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        CookieAuthenticationOptions primary = options.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        CookieAuthenticationOptions external = options.Get(IdentityCookieExtensions.ExternalSchemeName);
        Assert.True(callbackObserved);
        Assert.Equal(IdentityCookieExtensions.IdentityCookieName, primary.Cookie.Name);
        Assert.Equal(".example.test", primary.Cookie.Domain);
        Assert.Equal(securePolicy, primary.Cookie.SecurePolicy);
        Assert.False(primary.Cookie.HttpOnly);
        Assert.True(primary.SlidingExpiration);
        Assert.Equal(TimeSpan.FromDays(14), primary.ExpireTimeSpan);
        Assert.Equal("/auth/sign-in", primary.LoginPath);
        Assert.Equal("__Secure-Maliev.Identity.External", external.Cookie.Name);
        Assert.Equal(".example.test", external.Cookie.Domain);
        Assert.Equal(securePolicy, external.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromMinutes(10), external.ExpireTimeSpan);
    }

    [Theory]
    [InlineData(null, "https+http://legacy-maliev-auth-service/")]
    [InlineData("https://auth.example.test:7443", "https://auth.example.test:7443/")]
    public void LegacyTokenExchange_RegistersOneProviderAndValidatedAuthClient(string? configured, string expected)
    {
        var builder = Host.CreateApplicationBuilder();
        if (configured is not null)
        {
            builder.Configuration["Services:Auth:BaseUrl"] = configured;
        }

        builder.AddLegacyAuthServiceTokenExchange();
        builder.AddLegacyAuthServiceTokenExchange();
        using var host = builder.Build();
        Assert.Single(host.Services.GetServices<ILegacyServiceAccessTokenProvider>());
        using HttpClient client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(LegacyServiceAccessTokenProvider.HttpClientName);
        Assert.Equal(expected, client.BaseAddress!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }

    [Theory]
    [InlineData(" auth.example.test")]
    [InlineData("https://user:password@auth.example.test")]
    [InlineData("https://auth.example.test/path")]
    [InlineData("https://auth.example.test?query=true")]
    [InlineData("https://auth.example.test#fragment")]
    public void LegacyTokenExchange_RejectsUnsafeAuthOriginsAtClientResolution(string configured)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Services:Auth:BaseUrl"] = configured;
        builder.AddLegacyAuthServiceTokenExchange();
        using var host = builder.Build();
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(LegacyServiceAccessTokenProvider.HttpClientName));
    }

    [Fact]
    public void BatchServiceClients_PrefersAspireConnectionAndAppliesCallerConfiguration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:review"] = "https://aspire.example.test";
        builder.Configuration["Services:review:BaseUrl"] = "https://gke.example.test";
        builder.AddServiceClients(registrar => registrar.Add<IReviewClient, ReviewClient>(
            "review", client => client.DefaultRequestHeaders.Add("X-Contract", "verified")));

        using var host = builder.Build();
        ReviewClient client = Assert.IsType<ReviewClient>(host.Services.GetRequiredService<IReviewClient>());
        Assert.Equal("https://aspire.example.test/", client.Client.BaseAddress!.AbsoluteUri);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Client.Timeout);
        Assert.Equal("verified", Assert.Single(client.Client.DefaultRequestHeaders.GetValues("X-Contract")));
    }

    [Fact]
    public void BatchServiceClients_MissingEndpointFailsWithoutConstructingTypedClient()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceClients(registrar => registrar.Add<IReviewClient, ReviewClient>("missing"));
        using var host = builder.Build();
        var exception = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IReviewClient>());
        Assert.Contains("ConnectionStrings:missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Services:missing:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResilienceAndVersioning_ExposeConfiguredRuntimeContracts()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddBackgroundServiceResilience();
        builder.AddDefaultApiVersioning();
        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline("background-startup"));
        var options = host.Services.GetRequiredService<IOptions<ApiVersioningOptions>>().Value;
        Assert.Equal(new ApiVersion(1, 0), options.DefaultApiVersion);
        Assert.True(options.AssumeDefaultVersionWhenUnspecified);
        Assert.True(options.ReportApiVersions);
        Assert.IsType<UrlSegmentApiVersionReader>(options.ApiVersionReader);
    }

    [Fact]
    public void SnakeCaseModelNaming_ConvertsTablesColumnsKeysIndexesAndRelationships()
    {
        var options = new DbContextOptionsBuilder<NamingContext>()
            .UseNpgsql("Host=localhost;Database=synthetic;Username=synthetic")
            .Options;
        using var context = new NamingContext(options);
        var parent = context.Model.FindEntityType(typeof(CustomerOrder))!;
        var child = context.Model.FindEntityType(typeof(OrderLine))!;
        Assert.Equal("customer_orders", parent.GetTableName());
        Assert.Equal("customer_order_id", parent.FindProperty(nameof(CustomerOrder.CustomerOrderId))!.GetColumnName());
        Assert.Equal("pk_customer_orders", parent.FindPrimaryKey()!.GetName());
        Assert.Equal("ix_customer_orders_external_reference", Assert.Single(parent.GetIndexes()).GetDatabaseName());
        Assert.Equal("fk_order_lines_customer_orders_customer_order_id", Assert.Single(child.GetForeignKeys()).GetConstraintName());
    }

    public interface IReviewClient;

    public sealed class ReviewClient(HttpClient client) : IReviewClient
    {
        public HttpClient Client { get; } = client;
    }

    private sealed class CustomerOrder
    {
        public int CustomerOrderId { get; set; }
        public string ExternalReference { get; set; } = string.Empty;
        public List<OrderLine> OrderLines { get; set; } = [];
    }

    private sealed class OrderLine
    {
        public int OrderLineId { get; set; }
        public int CustomerOrderId { get; set; }
        public CustomerOrder CustomerOrder { get; set; } = null!;
    }

    private sealed class NamingContext(DbContextOptions<NamingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CustomerOrder>(entity =>
            {
                entity.ToTable("CustomerOrders");
                entity.HasKey(item => item.CustomerOrderId).HasName("PK_CustomerOrders");
                entity.HasIndex(item => item.ExternalReference).HasDatabaseName("IX_CustomerOrders_ExternalReference");
            });
            modelBuilder.Entity<OrderLine>(entity =>
            {
                entity.ToTable("OrderLines");
                entity.HasKey(item => item.OrderLineId).HasName("PK_OrderLines");
                entity.HasOne(item => item.CustomerOrder).WithMany(item => item.OrderLines)
                    .HasForeignKey(item => item.CustomerOrderId)
                    .HasConstraintName("FK_OrderLines_CustomerOrders_CustomerOrderId");
            });
            SnakeCaseNamingHelper.ApplySnakeCaseNaming(modelBuilder);
        }
    }
}
