using Microsoft.AspNetCore.Authorization;

namespace Maliev.Aspire.ServiceDefaults.Authorization;

/// <summary>
/// Authorization requirement that specifies a permission that must be satisfied for access.
/// Supports resource-scoped permissions, critical operation marking, and audit purpose tracking.
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// The required permission in GCP-style format (e.g., "customer.customers.create").
    /// </summary>
    public string Permission { get; }

    /// <summary>
    /// Optional template for resource path extraction from route values for resource-scoped authorization.
    /// </summary>
    public string? ResourcePathTemplate { get; }

    /// <summary>
    /// Indicates whether a live IAM service check is required instead of cached permissions.
    /// </summary>
    public bool RequireLiveCheck { get; }

    /// <summary>
    /// Indicates whether model validation should occur before authorization.
    /// </summary>
    public bool PreValidateModel { get; }

    /// <summary>
    /// Indicates whether this is a critical operation requiring elevated security checks.
    /// </summary>
    public bool IsCritical { get; }

    /// <summary>
    /// Optional audit purpose description for compliance logging.
    /// </summary>
    public string? AuditPurpose { get; }

    /// <summary>
    /// Initializes a new instance of the PermissionRequirement with the specified parameters.
    /// </summary>
    /// <param name="permission">The required permission in GCP-style format.</param>
    /// <param name="resourcePathTemplate">Optional template for resource path extraction.</param>
    /// <param name="requireLiveCheck">Whether to require live IAM service check.</param>
    /// <param name="preValidateModel">Whether to validate the model before authorization.</param>
    /// <param name="isCritical">Whether this is a critical operation.</param>
    /// <param name="auditPurpose">Optional audit purpose for compliance logging.</param>
    public PermissionRequirement(
        string permission,
        string? resourcePathTemplate = null,
        bool requireLiveCheck = false,
        bool preValidateModel = false,
        bool isCritical = false,
        string? auditPurpose = null)
    {
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
        ResourcePathTemplate = resourcePathTemplate;
        RequireLiveCheck = requireLiveCheck;
        PreValidateModel = preValidateModel;
        IsCritical = isCritical;
        AuditPurpose = auditPurpose;
    }
}
