using Maliev.MessagingContracts.Contracts.Iam;
using Maliev.MessagingContracts.Contracts.Shared;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Background service that handles IAM registration via RabbitMQ.
/// Publishes a PermissionRegistrationRequest through the raw singleton bus, which is consumed by the IAM service.
/// This infrastructure message deliberately bypasses any scoped transactional outbox because startup registration
/// has no business database unit of work to commit.
/// This is non-blocking, allowing services to start immediately without waiting for IAM response.
/// </summary>
public class BackgroundIAMRegistrationService : BackgroundService
{
    private readonly IEnumerable<IAMRegistrationService> _registrationServices;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundIAMRegistrationService> _logger;
    private readonly IAMRegistrationStatusTracker _statusTracker;
    private readonly IHostApplicationLifetime _applicationLifetime;

    /// <summary>
    /// Initializes a new instance of the BackgroundIAMRegistrationService.
    /// </summary>
    /// <param name="registrationServices">The collection of service-specific IAM registration implementations.</param>
    /// <param name="serviceProvider">Root service provider used to resolve the singleton MassTransit bus.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="statusTracker">Tracks registration status for health checks.</param>
    /// <param name="applicationLifetime">Host application lifetime for waiting until app is started.</param>
    public BackgroundIAMRegistrationService(
        IEnumerable<IAMRegistrationService> registrationServices,
        IServiceProvider serviceProvider,
        ILogger<BackgroundIAMRegistrationService> logger,
        IAMRegistrationStatusTracker statusTracker,
        IHostApplicationLifetime applicationLifetime)
    {
        _registrationServices = registrationServices ?? throw new ArgumentNullException(nameof(registrationServices));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statusTracker = statusTracker ?? throw new ArgumentNullException(nameof(statusTracker));
        _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
    }

    /// <summary>
    /// Executes the IAM registration process by publishing a message to RabbitMQ.
    /// Waits for the application to be fully started before attempting to publish.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token for graceful shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IAM registration background service started.");

        if (!_registrationServices.Any())
        {
            _logger.LogInformation("No IAM registration services configured.");
            _statusTracker.MarkRegistered();
            return;
        }

        try
        {
            // Wait for the application to be fully started (MassTransit bus will be ready at this point)
            // No artificial delay needed here — RabbitMQ reliably delivers messages when IAM service is ready,
            // and MassTransit's built-in retry with exponential backoff and jitter handles any startup races.
            await WaitForApplicationStartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("IAM registration cancelled during startup delay.");
            return;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("IAM registration cancelled before application startup completed");
            return;
        }

        _logger.LogInformation("Application started, beginning IAM registration via RabbitMQ for {ServiceCount} domains", _registrationServices.Count());
        _statusTracker.MarkAttempting();

        bool anyFailure = false;
        foreach (var registrationService in _registrationServices)
        {
            try
            {
                await RegisterServiceAsync(registrationService, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register service {ServiceName}", registrationService.ServiceName);
                anyFailure = true;
            }
        }

        if (anyFailure)
        {
            _statusTracker.MarkPartiallyRegistered(new AggregateException("One or more IAM registrations failed."));
        }
        else
        {
            _statusTracker.MarkRegistered();
        }
    }

    private async Task RegisterServiceAsync(IAMRegistrationService registrationService, CancellationToken stoppingToken)
    {
        var serviceName = registrationService.ServiceName;
        var permissions = registrationService.GetPermissionsForPublish().ToList();
        var roles = registrationService.GetRolesForPublish().ToList();

        if (!permissions.Any() && !roles.Any())
        {
            _logger.LogInformation("No permissions or roles to register for {ServiceName}", serviceName);
            return;
        }

        // Validate permissions format early
        foreach (var perm in permissions)
        {
            if (!IsValidPermissionFormat(perm.PermissionId))
            {
                throw new InvalidOperationException($"Invalid permission format for {serviceName}: {perm.PermissionId}. Expected service.resource.action");
            }
        }

        // Create the registration request message using shared contracts
        var request = new PermissionRegistrationRequest(
            MessageId: Guid.NewGuid(),
            MessageName: nameof(PermissionRegistrationRequest),
            MessageType: MessageType.Command,
            MessageVersion: "1.0.0",
            PublishedBy: serviceName,
            ConsumedBy: new[] { "iam-service" },
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            IsPublic: false,
            ServiceName: serviceName,
            Permissions: permissions,
            Roles: roles
        );

        // Simple retry logic for publishing
        int attempt = 0;
        const int maxAttempts = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Do not resolve scoped IPublishEndpoint here. Services that enable UseBusOutbox replace that
                // endpoint with an EF-backed publisher which requires DbContext.SaveChangesAsync. IAM registration
                // is startup infrastructure with no business transaction, so it must use the raw transport bus.
                var bus = _serviceProvider.GetRequiredService<IBus>();
                await bus.Publish(request, stoppingToken);

                _logger.LogInformation(
                    "Published IAM registration request for {ServiceName}: {PermissionCount} permissions, {RoleCount} roles",
                    serviceName, permissions.Count, roles.Count);

                return;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1 && !stoppingToken.IsCancellationRequested)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Failed to publish IAM registration for {ServiceName}. Retrying in {Delay} (Attempt {Attempt}/{Max})",
                    serviceName, delay, attempt + 1, maxAttempts);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Waits for the application to be fully started before proceeding.
    /// This ensures MassTransit and all other services are ready.
    /// </summary>
    private async Task WaitForApplicationStartAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource<bool>();

        using var registration = _applicationLifetime.ApplicationStarted.Register(() =>
        {
            tcs.TrySetResult(true);
        });

        if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            // Already started
            return;
        }

        await tcs.Task.WaitAsync(stoppingToken);
    }

    private static bool IsValidPermissionFormat(string permission)
    {
        var parts = permission.Split('.');
        return parts.Length == 3 && parts.All(seg => !string.IsNullOrWhiteSpace(seg));
    }
}

// REMOVED local DTOs as they are now replaced by shared contracts from Maliev.MessagingContracts

/// <summary>
/// Tracks IAM registration status for health check reporting
/// </summary>
public class IAMRegistrationStatusTracker
{
    private volatile RegistrationStatus _status = RegistrationStatus.Pending;
    private Exception? _lastException;

    /// <summary>
    /// Gets whether registration has completed (message published to RabbitMQ).
    /// </summary>
    public bool IsRegistered => _status == RegistrationStatus.Registered;

    /// <summary>
    /// Gets the current registration status.
    /// </summary>
    public RegistrationStatus Status => _status;

    /// <summary>
    /// Gets the last exception if any occurred during registration.
    /// </summary>
    public Exception? LastException => _lastException;

    /// <summary>
    /// Marks the registration as currently attempting.
    /// </summary>
    public void MarkAttempting() => _status = RegistrationStatus.Attempting;

    /// <summary>
    /// Marks the registration as successfully completed.
    /// </summary>
    public void MarkRegistered() => _status = RegistrationStatus.Registered;

    /// <summary>
    /// Marks as partially registered when max retries exceeded.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    public void MarkPartiallyRegistered(Exception ex)
    {
        _status = RegistrationStatus.PartiallyRegistered;
        _lastException = ex;
    }

    /// <summary>
    /// Marks the registration as failed unrecoverably.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    public void MarkFailed(Exception ex)
    {
        _status = RegistrationStatus.Failed;
        _lastException = ex;
    }
}

/// <summary>
/// IAM registration status enum
/// </summary>
public enum RegistrationStatus
{
    /// <summary>Initial state, not started yet.</summary>
    Pending,

    /// <summary>Currently publishing registration request.</summary>
    Attempting,

    /// <summary>Successfully published to RabbitMQ.</summary>
    Registered,

    /// <summary>Failed to publish but service continued.</summary>
    PartiallyRegistered,

    /// <summary>Unrecoverable failure.</summary>
    Failed
}
