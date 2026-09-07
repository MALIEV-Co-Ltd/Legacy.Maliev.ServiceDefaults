using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class IAMRegistrationHealthCheckTests
{
    [Fact]
    public void Constructor_RejectsMissingTracker()
    {
        Assert.Throws<ArgumentNullException>(() => new IAMRegistrationHealthCheck(null!));
    }

    [Theory]
    [InlineData(RegistrationStatus.Pending, HealthStatus.Degraded)]
    [InlineData(RegistrationStatus.Attempting, HealthStatus.Degraded)]
    [InlineData(RegistrationStatus.Registered, HealthStatus.Healthy)]
    [InlineData(RegistrationStatus.PartiallyRegistered, HealthStatus.Degraded)]
    [InlineData(RegistrationStatus.Failed, HealthStatus.Degraded)]
    public async Task CheckHealthAsync_ReportsTrackedRegistrationState(
        RegistrationStatus state,
        HealthStatus expected)
    {
        var tracker = new IAMRegistrationStatusTracker();
        var failure = new InvalidOperationException("controlled registration failure");
        SetState(tracker, state, failure);
        var check = new IAMRegistrationHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(expected, result.Status);
        Assert.Equal(state.ToString(), result.Data["RegistrationStatus"]);
        if (state is RegistrationStatus.PartiallyRegistered or RegistrationStatus.Failed)
        {
            Assert.Same(failure, result.Exception);
            Assert.Contains("controlled registration failure", result.Description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tracker_TransitionsAndRetainsFailureEvidence()
    {
        var tracker = new IAMRegistrationStatusTracker();
        Assert.Equal(RegistrationStatus.Pending, tracker.Status);
        Assert.False(tracker.IsRegistered);

        tracker.MarkAttempting();
        Assert.Equal(RegistrationStatus.Attempting, tracker.Status);
        tracker.MarkRegistered();
        Assert.True(tracker.IsRegistered);

        var partial = new InvalidOperationException("partial");
        tracker.MarkPartiallyRegistered(partial);
        Assert.Equal(RegistrationStatus.PartiallyRegistered, tracker.Status);
        Assert.Same(partial, tracker.LastException);

        var failed = new InvalidOperationException("failed");
        tracker.MarkFailed(failed);
        Assert.Equal(RegistrationStatus.Failed, tracker.Status);
        Assert.Same(failed, tracker.LastException);
    }

    private static void SetState(
        IAMRegistrationStatusTracker tracker,
        RegistrationStatus state,
        Exception failure)
    {
        switch (state)
        {
            case RegistrationStatus.Pending:
                return;
            case RegistrationStatus.Attempting:
                tracker.MarkAttempting();
                return;
            case RegistrationStatus.Registered:
                tracker.MarkRegistered();
                return;
            case RegistrationStatus.PartiallyRegistered:
                tracker.MarkPartiallyRegistered(failure);
                return;
            case RegistrationStatus.Failed:
                tracker.MarkFailed(failure);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }
}
