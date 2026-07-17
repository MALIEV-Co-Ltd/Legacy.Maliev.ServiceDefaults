namespace Maliev.Aspire.ServiceDefaults.Authorization;

/// <summary>
/// Standard interface for recording authorization-related business metrics.
/// </summary>
public interface IAuthMetrics
{
    /// <summary>
    /// Records a successful authorization check.
    /// </summary>
    /// <param name="permission">The permission that was checked.</param>
    void RecordSuccess(string permission);

    /// <summary>
    /// Records a failed authorization check.
    /// </summary>
    /// <param name="permission">The permission that was checked.</param>
    /// <param name="reason">The reason for failure.</param>
    void RecordFailure(string permission, string reason);
}
