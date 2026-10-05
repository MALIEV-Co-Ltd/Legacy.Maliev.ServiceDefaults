using System.Data.Common;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class MessagingRegistrationBehaviorTests
{
    [Theory]
    [InlineData("MassTransit:UseInMemory")]
    [InlineData("MASSTRANSIT_INMEMORY")]
    public async Task TestingTransport_DeliversMessageToRegisteredConsumer_WithoutRabbit(string flag)
    {
        var builder = Builder("Testing");
        builder.Configuration[flag] = "true";
        var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton(receipt);
        builder.AddMassTransitWithRabbitMq(configure => configure.AddConsumer<ReviewConsumer>());
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(new ReviewMessage("controlled-delivery"));
            Assert.Equal("controlled-delivery", await receipt.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("loopback", bus.Address.Scheme);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("amqp://localhost:5673", "localhost", 5673, null)]
    [InlineData("amqps://localhost:5674", "localhost", 5674, null)]
    [InlineData("host=localhost;port=5675;username=synthetic", "localhost", 5675, "password")]
    [InlineData("host=localhost;port=5676;user=synthetic", "localhost", 5676, "pass")]
    // MassTransit normalizes its default AMQP port out of the published bus URI.
    [InlineData("localhost", "localhost", -1, null)]
    public void RabbitRegistration_UsesConfiguredDestination_WithoutStartingTransport(string connection, string hostname, int port, string? passwordKey)
    {
        if (passwordKey is not null)
        {
            var syntheticConnection = new DbConnectionStringBuilder { ConnectionString = connection };
            syntheticConnection[passwordKey] = Guid.NewGuid().ToString("N");
            connection = syntheticConnection.ConnectionString;
        }
        var builder = Builder(Environments.Production);
        builder.Configuration["ConnectionStrings:RabbitMQ"] = connection;
        builder.AddMassTransitWithRabbitMq();
        using var host = builder.Build();
        // Creating the bus configures it; StartAsync is deliberately never called.
        var bus = host.Services.GetRequiredService<IBus>();
        Assert.Equal(hostname, bus.Address.Host);
        Assert.Equal(port, bus.Address.Port);
        Assert.True(host.Services.GetRequiredService<IOptions<MassTransitHostOptions>>().Value.WaitUntilStarted);
    }

    [Fact]
    public void ProductionTransport_MissingConnection_FailsClosedWithoutPrintingOtherValues()
    {
        var builder = Builder(Environments.Production);
        builder.Configuration["ConnectionStrings:Other"] = "synthetic-private-value";
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddMassTransitWithRabbitMq());
        Assert.Contains("Other", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-value", exception.Message, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder Builder(string environment)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        return builder;
    }

    public sealed record ReviewMessage(string Value);
    public sealed class ReviewConsumer(TaskCompletionSource<string> receipt) : IConsumer<ReviewMessage>
    {
        public Task Consume(ConsumeContext<ReviewMessage> context)
        {
            receipt.TrySetResult(context.Message.Value);
            return Task.CompletedTask;
        }
    }
}
