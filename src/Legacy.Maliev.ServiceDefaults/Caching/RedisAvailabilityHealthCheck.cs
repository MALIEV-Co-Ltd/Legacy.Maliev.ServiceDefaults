using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Maliev.Aspire.ServiceDefaults.Caching;

/// <summary>Reports whether the Redis multiplexer remains connected for readiness checks.</summary>
public sealed class RedisAvailabilityHealthCheck(Func<bool> isAvailable) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return Task.FromResult(isAvailable()
                ? HealthCheckResult.Healthy("Redis is connected.")
                : HealthCheckResult.Unhealthy("Redis is disconnected."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Redis availability check failed.", exception));
        }
    }
}
