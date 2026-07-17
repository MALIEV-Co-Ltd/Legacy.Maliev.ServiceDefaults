namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Interface for service-specific permission definitions
/// </summary>
public interface IServicePermissions
{
    /// <summary>
    /// Gets all permissions defined by the service
    /// </summary>
    IEnumerable<string> All { get; }
}
