using System.Collections.Concurrent;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.MessagingContracts.Contracts.Iam;
using Maliev.MessagingContracts.Contracts.Shared;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class BackgroundIamPublishedContractTests
{
    [Fact]
    public async Task PendingApplicationStart_DoesNotPublishUntilActualHostStartsThenDeliversPrivateEnvelope()
    {
        await using var fixture = await PublishedFixture.CreateAsync([ValidRegistration()]);
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(RegistrationStatus.Pending, fixture.Status.Status);
        Assert.False(fixture.Receipt.Task.IsCompleted);
        fixture.AssertPublishCount(0);

        var before = DateTimeOffset.UtcNow;
        await fixture.Host.StartAsync();
        await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.AssertPublishCount(1);
        var message = await fixture.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var after = DateTimeOffset.UtcNow;
        Assert.True(fixture.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.Equal(RegistrationStatus.Registered, fixture.Status.Status);
        Assert.Null(fixture.Status.LastException);
        Assert.Equal(0, fixture.OutboxResolutionAttempts);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.NotEqual(Guid.Empty, message.CorrelationId);
        Assert.NotEqual(message.MessageId, message.CorrelationId);
        Assert.Null(message.CausationId);
        Assert.Equal("PermissionRegistrationRequest", message.MessageName);
        Assert.Equal(MessageType.Command, message.MessageType);
        Assert.Equal("1.0.0", message.MessageVersion);
        Assert.Equal("fixture-customer", message.PublishedBy);
        Assert.Equal(["iam-service"], message.ConsumedBy);
        Assert.Equal("fixture-customer", message.ServiceName);
        Assert.False(message.IsPublic);
        Assert.Equal(TimeSpan.Zero, message.OccurredAtUtc.Offset);
        Assert.InRange(message.OccurredAtUtc, before, after);
        var permission = Assert.Single(message.Permissions);
        Assert.Equal("customer.records.read", permission.PermissionId);
        Assert.Equal("อ่านข้อมูลลูกค้า", permission.Description);
        var role = Assert.Single(message.Roles);
        Assert.Equal("customer-reader", role.RoleId);
        Assert.Equal("พนักงานอ่านข้อมูล", role.Description);
        Assert.Equal(["customer.records.read"], role.PermissionIds);
    }

    [Fact]
    public async Task InvalidFirstRegistration_StillPublishesValidLaterDomainAndReportsPartialStatus()
    {
        var invalid = new PublishedRegistration("fixture-invalid",
            [new PermissionRegistration { PermissionId = "invalid-format", Description = "invalid fixture" }], []);
        await using var fixture = await PublishedFixture.CreateAsync([invalid, ValidRegistration()]);
        await fixture.Host.StartAsync();
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.AssertPublishCount(1);
        var message = await fixture.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fixture-customer", message.ServiceName);
        Assert.Equal("customer.records.read", Assert.Single(message.Permissions).PermissionId);
        Assert.Equal(RegistrationStatus.PartiallyRegistered, fixture.Status.Status);
        Assert.False(fixture.Status.IsRegistered);
        Assert.IsType<AggregateException>(fixture.Status.LastException);
        Assert.Equal(0, fixture.OutboxResolutionAttempts);
    }

    [Fact]
    public async Task EmptyDomainAndRoleOnlyDomain_PublishOnlyRoleAndNormalizeMissingDescriptionsAndPermissions()
    {
        var empty = new PublishedRegistration("fixture-empty", [], []);
        var roleOnly = new PublishedRegistration("fixture-role", [],
            [new RoleRegistration { RoleId = "fixture-role-without-permissions" }]);
        await using var fixture = await PublishedFixture.CreateAsync([empty, roleOnly]);
        await fixture.Host.StartAsync();
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.AssertPublishCount(1);
        var message = await fixture.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fixture-role", message.ServiceName);
        Assert.Empty(message.Permissions);
        var role = Assert.Single(message.Roles);
        Assert.Equal("fixture-role-without-permissions", role.RoleId);
        Assert.Equal(string.Empty, role.Description);
        Assert.Empty(role.PermissionIds);
        Assert.Equal(RegistrationStatus.Registered, fixture.Status.Status);
        Assert.Equal(0, fixture.OutboxResolutionAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrInvalidDomain_CompletesWithoutAnyPublishAttempt(bool invalid)
    {
        var registration = new PublishedRegistration("fixture-no-publish",
            invalid ? [new PermissionRegistration { PermissionId = "invalid-format" }] : [], []);
        await using var fixture = await PublishedFixture.CreateAsync([registration]);
        await fixture.Host.StartAsync();
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.AssertPublishCount(0);
        Assert.False(fixture.Receipt.Task.IsCompleted);
        Assert.Empty(fixture.Messages);
        Assert.Equal(invalid ? RegistrationStatus.PartiallyRegistered : RegistrationStatus.Registered, fixture.Status.Status);
        Assert.Equal(0, fixture.OutboxResolutionAttempts);
    }

    [Fact]
    public async Task StopBeforeApplicationStart_CompletesWithoutPublishingOrMarkingRegistered()
    {
        await using var fixture = await PublishedFixture.CreateAsync([ValidRegistration()]);
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Service.StopAsync(deadline.Token);
        await fixture.Execution.WaitAsync(deadline.Token);
        fixture.AssertPublishCount(0);
        Assert.False(fixture.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.False(fixture.Receipt.Task.IsCompleted);
        Assert.Empty(fixture.Messages);
        Assert.Equal(RegistrationStatus.Pending, fixture.Status.Status);
        Assert.False(fixture.Status.IsRegistered);
        Assert.Equal(0, fixture.OutboxResolutionAttempts);
    }

    private static PublishedRegistration ValidRegistration() => new("fixture-customer",
        [new PermissionRegistration { PermissionId = "customer.records.read", Description = "อ่านข้อมูลลูกค้า" }],
        [new RoleRegistration
        {
            RoleId = "customer-reader", Description = "พนักงานอ่านข้อมูล",
            PermissionIds = ["customer.records.read"]
        }]);

    private sealed class PublishedRegistration(string name,
        IEnumerable<PermissionRegistration> permissions, IEnumerable<RoleRegistration> roles)
        : IAMRegistrationService(new ConfigurationBuilder().Build(), NullLogger.Instance, name)
    {
        protected override IEnumerable<PermissionRegistration> GetPermissions() => permissions;
        protected override IEnumerable<RoleRegistration> GetPredefinedRoles() => roles;
    }

    private sealed class PublishedFixture : IAsyncDisposable
    {
        private readonly IBusControl _bus;
        private readonly ConnectHandle _publishObserverConnection;
        private readonly PublishCounter _publishCounter = new();
        private int _outboxResolutionAttempts;
        public IHost Host { get; }
        public IHostApplicationLifetime Lifetime { get; }
        public IAMRegistrationStatusTracker Status { get; } = new();
        public EntryLogger Logger { get; } = new();
        public BackgroundIAMRegistrationService Service { get; }
        public Task Execution => Service.ExecuteTask ?? throw new InvalidOperationException("Fixture service has not started.");
        public TaskCompletionSource<PermissionRegistrationRequest> Receipt { get; }
        public ConcurrentQueue<PermissionRegistrationRequest> Messages { get; }
        public int OutboxResolutionAttempts => Volatile.Read(ref _outboxResolutionAttempts);

        private PublishedFixture(IAMRegistrationService[] registrations)
        {
            Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Messages = new();
            _bus = Bus.Factory.CreateUsingInMemory(configuration =>
                configuration.ReceiveEndpoint("fixture-iam-" + Guid.NewGuid().ToString("N"), endpoint =>
                    endpoint.Handler<PermissionRegistrationRequest>(context =>
                    {
                        Messages.Enqueue(context.Message);
                        Receipt.TrySetResult(context.Message);
                        return Task.CompletedTask;
                    })));
            _publishObserverConnection = _bus.ConnectPublishObserver(_publishCounter);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.Services.AddSingleton<IBus>(_bus);
            builder.Services.AddScoped<IPublishEndpoint>(_ =>
            {
                Interlocked.Increment(ref _outboxResolutionAttempts);
                throw new InvalidOperationException("Startup registration must not resolve the scoped outbox.");
            });
            Host = builder.Build();
            Lifetime = Host.Services.GetRequiredService<IHostApplicationLifetime>();
            Service = new(registrations, Host.Services, Logger, Status, Lifetime);
        }

        // Publish observers run within the awaited Publish call, so terminal service
        // execution proves cardinality without racing asynchronous receive delivery.
        public void AssertPublishCount(int expected)
        {
            Assert.Equal(expected, _publishCounter.Attempts);
            Assert.Equal(expected, _publishCounter.Completions);
            Assert.Equal(0, _publishCounter.Faults);
        }

        public static async Task<PublishedFixture> CreateAsync(IAMRegistrationService[] registrations)
        {
            var fixture = new PublishedFixture(registrations);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await fixture._bus.StartAsync(deadline.Token);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Service.StopAsync(deadline.Token); }
            finally
            {
                Service.Dispose();
                try { await _bus.StopAsync(deadline.Token); }
                finally
                {
                    _publishObserverConnection.Disconnect();
                    try { await Host.StopAsync(deadline.Token); }
                    finally { Host.Dispose(); }
                }
            }
        }
    }

    private sealed class PublishCounter : IPublishObserver
    {
        private int _attempts;
        private int _completions;
        private int _faults;
        public int Attempts => Volatile.Read(ref _attempts);
        public int Completions => Volatile.Read(ref _completions);
        public int Faults => Volatile.Read(ref _faults);

        public Task PrePublish<T>(PublishContext<T> context) where T : class
        {
            if (context.Message is PermissionRegistrationRequest)
                Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }

        public Task PostPublish<T>(PublishContext<T> context) where T : class
        {
            if (context.Message is PermissionRegistrationRequest)
                Interlocked.Increment(ref _completions);
            return Task.CompletedTask;
        }

        public Task PublishFault<T>(PublishContext<T> context, Exception exception) where T : class
        {
            if (context.Message is PermissionRegistrationRequest)
                Interlocked.Increment(ref _faults);
            return Task.CompletedTask;
        }
    }

    private sealed class EntryLogger : ILogger<BackgroundIAMRegistrationService>
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information && formatter(state, exception) == "IAM registration background service started.")
                Entered.TrySetResult(true);
        }
    }
}
