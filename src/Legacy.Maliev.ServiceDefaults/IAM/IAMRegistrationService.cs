using Maliev.MessagingContracts.Contracts.Iam;
using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Base class for services to define their permissions and roles for IAM registration.
/// Registration is performed by BackgroundIAMRegistrationService via RabbitMQ publishing.
/// </summary>
public abstract class IAMRegistrationService
{
    private readonly ILogger _logger;
    private readonly string _serviceName;

    /// <summary>
    /// Gets the service name used for IAM registration.
    /// </summary>
    public string ServiceName => _serviceName;

    /// <summary>
    /// Gets the application configuration.
    /// </summary>
    public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="IAMRegistrationService"/> class.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="serviceName">The service name.</param>
    protected IAMRegistrationService(
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        ILogger logger,
        string serviceName)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
    }

    /// <summary>
    /// Gets the permissions to register. Override in derived classes to define service-specific permissions.
    /// </summary>
    /// <returns>Collection of permission registrations.</returns>
    protected abstract IEnumerable<PermissionRegistration> GetPermissions();

    /// <summary>
    /// Gets the predefined roles to register. Override in derived classes to define service-specific roles.
    /// </summary>
    /// <returns>Collection of role registrations.</returns>
    protected abstract IEnumerable<RoleRegistration> GetPredefinedRoles();

    /// <summary>
    /// Gets permissions converted to DTOs for RabbitMQ publishing.
    /// </summary>
    /// <returns>Collection of permission DTOs.</returns>
    public IEnumerable<PermissionRegistrationRequestPermissionsItem> GetPermissionsForPublish()
    {
        return GetPermissions().Select(p => new PermissionRegistrationRequestPermissionsItem(
            p.PermissionId,
            p.Description ?? string.Empty));
    }

    /// <summary>
    /// Gets roles converted to DTOs for RabbitMQ publishing.
    /// </summary>
    /// <returns>Collection of role DTOs.</returns>
    public IEnumerable<PermissionRegistrationRequestRolesItem> GetRolesForPublish()
    {
        return GetPredefinedRoles().Select(r => new PermissionRegistrationRequestRolesItem(
            r.RoleId,
            r.Description ?? string.Empty,
            r.PermissionIds?.ToList() ?? new List<string>()));
    }
}

/// <summary>
/// Represents a permission to be registered with IAM.
/// </summary>
public class PermissionRegistration
{
    /// <summary>
    /// The permission ID in service.resource.action format.
    /// </summary>
    public required string PermissionId { get; set; }

    /// <summary>
    /// Human-readable description of the permission.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Represents a role to be registered with IAM.
/// </summary>
public class RoleRegistration
{
    /// <summary>
    /// The unique role identifier.
    /// </summary>
    public required string RoleId { get; set; }

    /// <summary>
    /// Human-readable description of the role.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// List of permission IDs assigned to this role.
    /// </summary>
    public IList<string>? PermissionIds { get; set; }

    /// <summary>
    /// Indicates whether this is a custom role (vs predefined).
    /// </summary>
    public bool IsCustom { get; set; } = false;
}
