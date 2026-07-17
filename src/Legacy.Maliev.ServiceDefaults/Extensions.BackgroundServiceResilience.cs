using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding resilience to background services.
/// </summary>
public static class BackgroundServiceResilienceExtensions
{
    /// <summary>
    /// Adds standard resilience pipeline for background service startup operations.
    /// Includes retry with exponential backoff, circuit breaker, and timeout.
    /// </summary>
    public static IServiceCollection AddBackgroundServiceResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline("background-startup", builder =>
        {
            builder
                // Retry with exponential backoff and jitter
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true, // Prevents thundering herd problem
                    MaxDelay = TimeSpan.FromSeconds(30)
                })
                // Circuit breaker to prevent hammering failing dependencies
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    BreakDuration = TimeSpan.FromSeconds(30),
                    FailureRatio = 0.5,
                    MinimumThroughput = 3,
                    SamplingDuration = TimeSpan.FromSeconds(60)
                })
                // Overall timeout for the operation
                .AddTimeout(TimeSpan.FromMinutes(2));
        });

        return services;
    }
}
