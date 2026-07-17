using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Health check that reports IAM registration status
/// </summary>
public class IAMRegistrationHealthCheck : IHealthCheck
{
    private readonly IAMRegistrationStatusTracker _statusTracker;

    /// <summary>
    /// Initializes a new instance of the IAMRegistrationHealthCheck with the specified status tracker.
    /// </summary>
    /// <param name="statusTracker">The IAM registration status tracker to query.</param>
    public IAMRegistrationHealthCheck(IAMRegistrationStatusTracker statusTracker)
    {
        _statusTracker = statusTracker ?? throw new ArgumentNullException(nameof(statusTracker));
    }

    /// <summary>
    /// Checks the IAM registration status and returns an appropriate health check result.
    /// Reports Healthy when registered, Degraded while registration is pending, attempting, or failed,
    /// and Unhealthy only for unexpected status values.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A health check result reflecting the current IAM registration status.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = _statusTracker.Status;
        var data = new Dictionary<string, object>
        {
            ["RegistrationStatus"] = status.ToString()
        };

        return status switch
        {
            RegistrationStatus.Registered =>
                Task.FromResult(HealthCheckResult.Healthy("IAM registration successful", data)),

            RegistrationStatus.Pending =>
                Task.FromResult(HealthCheckResult.Degraded("IAM registration pending application startup", null, data)),

            RegistrationStatus.Attempting =>
                Task.FromResult(HealthCheckResult.Degraded("IAM registration in progress (publishing to RabbitMQ)", null, data)),

            RegistrationStatus.PartiallyRegistered =>
                Task.FromResult(HealthCheckResult.Degraded(
                    $"IAM registration failed after multiple retries. Service running with cached permissions only. Error: {_statusTracker.LastException?.Message}",
                    _statusTracker.LastException,
                    data)),

            RegistrationStatus.Failed =>
                Task.FromResult(HealthCheckResult.Degraded(
                    $"IAM registration failed: {_statusTracker.LastException?.Message}",
                    _statusTracker.LastException,
                    data)),

            _ => Task.FromResult(HealthCheckResult.Unhealthy("Unknown registration status", null, data))
        };
    }
}
