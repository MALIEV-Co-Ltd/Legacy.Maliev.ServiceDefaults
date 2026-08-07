using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;

namespace Maliev.Aspire.Tests.Unit;

/// <summary>Regression tests for trusted proxy configuration.</summary>
public sealed class ForwardedHeadersSecurityTests
{
    [Fact]
    public void ProductionWithoutProxyConfiguration_DoesNotTrustForwardedHeaders()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });

        builder.AddStandardMiddleware();
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Empty(options.KnownProxies);
        Assert.DoesNotContain(options.KnownIPNetworks, network => network.Contains(IPAddress.Parse("10.0.0.0")));
    }

    [Fact]
    public void ProductionUsesOnlyConfiguredProxyAddresses()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration["ForwardedHeaders:KnownProxies:0"] = "10.0.0.10";
        builder.AddStandardMiddleware();
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Contains(IPAddress.Parse("10.0.0.10"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Loopback, options.KnownProxies);
    }

    [Fact]
    public void InvalidProxyAddress_FailsClosedDuringConfiguration()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration["ForwardedHeaders:KnownProxies:0"] = "not-an-ip";
        builder.AddStandardMiddleware();

        using var provider = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);
        Assert.Contains("ForwardedHeaders:KnownProxies", exception.Message, StringComparison.Ordinal);
    }
}
