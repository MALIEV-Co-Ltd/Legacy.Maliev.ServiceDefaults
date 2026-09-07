using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class BackgroundIAMRegistrationServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoRegistrations_CompletesAsRegistered()
    {
        var tracker = new IAMRegistrationStatusTracker();
        var service = CreateService([], tracker, new ControlledLifetime(started: false));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => tracker.IsRegistered);

        Assert.True(tracker.IsRegistered);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WithEmptyRegistrationAfterApplicationStart_CompletesAsRegistered()
    {
        var tracker = new IAMRegistrationStatusTracker();
        var registration = new ControlledRegistration([], []);
        var service = CreateService([registration], tracker, new ControlledLifetime(started: true));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => tracker.IsRegistered);

        Assert.Equal(RegistrationStatus.Registered, tracker.Status);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WithInvalidPermission_RecordsPartialRegistrationWithoutPublishing()
    {
        var tracker = new IAMRegistrationStatusTracker();
        var registration = new ControlledRegistration(
            [new PermissionRegistration { PermissionId = "invalid", Description = "invalid contract" }],
            []);
        var service = CreateService([registration], tracker, new ControlledLifetime(started: true));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => tracker.Status == RegistrationStatus.PartiallyRegistered);

        Assert.IsType<AggregateException>(tracker.LastException);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopBeforeApplicationStart_CancelsPendingRegistrationCleanly()
    {
        var tracker = new IAMRegistrationStatusTracker();
        var registration = new ControlledRegistration([], []);
        var service = CreateService([registration], tracker, new ControlledLifetime(started: false));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(RegistrationStatus.Pending, tracker.Status);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var tracker = new IAMRegistrationStatusTracker();
        var lifetime = new ControlledLifetime(started: false);
        var logger = NullLogger<BackgroundIAMRegistrationService>.Instance;
        var registrations = Array.Empty<IAMRegistrationService>();

        Assert.Throws<ArgumentNullException>(() => new BackgroundIAMRegistrationService(null!, services, logger, tracker, lifetime));
        Assert.Throws<ArgumentNullException>(() => new BackgroundIAMRegistrationService(registrations, null!, logger, tracker, lifetime));
        Assert.Throws<ArgumentNullException>(() => new BackgroundIAMRegistrationService(registrations, services, null!, tracker, lifetime));
        Assert.Throws<ArgumentNullException>(() => new BackgroundIAMRegistrationService(registrations, services, logger, null!, lifetime));
        Assert.Throws<ArgumentNullException>(() => new BackgroundIAMRegistrationService(registrations, services, logger, tracker, null!));
    }

    [Fact]
    public void RegistrationService_MapsPermissionsAndRolesToWireContracts()
    {
        var registration = new ControlledRegistration(
            [new PermissionRegistration { PermissionId = "orders.records.read", Description = null }],
            [new RoleRegistration
            {
                RoleId = "orders-reviewer",
                Description = null,
                PermissionIds = null
            }]);

        var permission = Assert.Single(registration.GetPermissionsForPublish());
        Assert.Equal("orders.records.read", permission.PermissionId);
        Assert.Equal(string.Empty, permission.Description);
        var role = Assert.Single(registration.GetRolesForPublish());
        Assert.Equal("orders-reviewer", role.RoleId);
        Assert.Equal(string.Empty, role.Description);
        Assert.Empty(role.PermissionIds);
    }

    [Fact]
    public void RegistrationService_RejectsMissingConstructorValues()
    {
        Assert.Throws<ArgumentNullException>(() => new InvalidControlledRegistration(null, NullLogger.Instance, "service"));
        Assert.Throws<ArgumentNullException>(() => new InvalidControlledRegistration(new ConfigurationBuilder().Build(), null, "service"));
        Assert.Throws<ArgumentNullException>(() => new InvalidControlledRegistration(new ConfigurationBuilder().Build(), NullLogger.Instance, null));
    }

    private static BackgroundIAMRegistrationService CreateService(
        IEnumerable<IAMRegistrationService> registrations,
        IAMRegistrationStatusTracker tracker,
        IHostApplicationLifetime lifetime)
    {
        return new BackgroundIAMRegistrationService(
            registrations,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<BackgroundIAMRegistrationService>.Instance,
            tracker,
            lifetime);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ControlledRegistration(
        IEnumerable<PermissionRegistration> permissions,
        IEnumerable<RoleRegistration> roles)
        : IAMRegistrationService(
            new ConfigurationBuilder().Build(),
            NullLogger.Instance,
            "controlled-service")
    {
        protected override IEnumerable<PermissionRegistration> GetPermissions() => permissions;

        protected override IEnumerable<RoleRegistration> GetPredefinedRoles() => roles;
    }

    private sealed class ControlledLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public ControlledLifetime(bool started)
        {
            if (started)
            {
                _started.Cancel();
            }
        }

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class InvalidControlledRegistration(
        IConfiguration? configuration,
        Microsoft.Extensions.Logging.ILogger? logger,
        string? serviceName)
        : IAMRegistrationService(configuration!, logger!, serviceName!)
    {
        protected override IEnumerable<PermissionRegistration> GetPermissions() => [];

        protected override IEnumerable<RoleRegistration> GetPredefinedRoles() => [];
    }
}
