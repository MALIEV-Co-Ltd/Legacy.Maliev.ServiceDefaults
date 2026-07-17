using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// GCP-style IAM roles for MALIEV services (MALIEV-specific, not GCP platform).
/// Uses "maliev.iam.role" claim type (NOT "gcp.iam.role").
/// </summary>
public static class MalievIamRoles
{
    // Platform-level role (assigned to first @maliev.com user)
    /// <summary>
    /// Platform Owner role with full control of the MALIEV platform.
    /// Automatically assigned to the first @maliev.com user to sign in.
    /// </summary>
    public const string PlatformOwner = "roles.platform.owner";

    // Service-level roles
    /// <summary>
    /// Service administrator role with full control over a specific service.
    /// </summary>
    public const string ServiceAdmin = "roles/maliev.serviceAdmin";

    /// <summary>
    /// Service viewer role with read-only access to a specific service.
    /// </summary>
    public const string ServiceViewer = "roles/maliev.serviceViewer";

    /// <summary>
    /// Service editor role with read-write access to a specific service.
    /// </summary>
    public const string ServiceEditor = "roles/maliev.serviceEditor";

    // Resource-level roles
    /// <summary>
    /// Customer administrator role with full control over customer resources.
    /// </summary>
    public const string CustomerAdmin = "roles/maliev.customer.admin";

    /// <summary>
    /// Customer viewer role with read-only access to customer resources.
    /// </summary>
    public const string CustomerViewer = "roles/maliev.customer.viewer";

    /// <summary>
    /// Order administrator role with full control over order resources.
    /// </summary>
    public const string OrderAdmin = "roles/maliev.order.admin";

    /// <summary>
    /// Order viewer role with read-only access to order resources.
    /// </summary>
    public const string OrderViewer = "roles/maliev.order.viewer";

    // System roles
    /// <summary>
    /// System service account role for inter-service communication.
    /// </summary>
    public const string SystemServiceAccount = "roles/maliev.system.serviceAccount";

    /// <summary>
    /// Checks if user has platform owner role (full control of MALIEV platform).
    /// </summary>
    /// <param name="user">The claims principal representing the user.</param>
    /// <returns>True if the user has platform owner role, false otherwise.</returns>
    public static bool IsPlatformOwner(ClaimsPrincipal user)
    {
        return user.HasClaim("maliev.iam.role", PlatformOwner);
    }

    /// <summary>
    /// Gets all MALIEV IAM roles from user claims.
    /// </summary>
    /// <param name="user">The claims principal representing the user.</param>
    /// <returns>List of MALIEV IAM role identifiers.</returns>
    public static List<string> GetUserRoles(ClaimsPrincipal user)
    {
        return user.Claims
            .Where(c => c.Type == "maliev.iam.role")
            .Select(c => c.Value)
            .ToList();
    }
}

/// <summary>
/// Authorization requirement for MALIEV IAM roles.
/// </summary>
public class MalievIamRoleRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Gets the required roles for authorization.
    /// </summary>
    public string[] RequiredRoles { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MalievIamRoleRequirement"/> class.
    /// </summary>
    /// <param name="roles">The required MALIEV IAM role identifiers.</param>
    public MalievIamRoleRequirement(params string[] roles)
    {
        RequiredRoles = roles;
    }
}

/// <summary>
/// Handler for MALIEV IAM role validation (GCP-style but MALIEV-specific).
/// Validates roles using "maliev.iam.role" claim type.
/// </summary>
public class MalievIamRoleHandler : AuthorizationHandler<MalievIamRoleRequirement>
{
    /// <summary>
    /// Makes a decision if authorization is allowed based on MALIEV IAM roles.
    /// </summary>
    /// <param name="context">The authorization context.</param>
    /// <param name="requirement">The requirement to evaluate.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MalievIamRoleRequirement requirement)
    {
        // MALIEV-specific claim type (not 'gcp.iam.role')
        var userRoles = context.User.Claims
            .Where(c => c.Type == "maliev.iam.role")
            .Select(c => c.Value)
            .ToList();

        if (requirement.RequiredRoles.Any(role => userRoles.Contains(role)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Bootstrap service to assign Platform Owner role to first @maliev.com user.
/// This provides initial platform access and allows subsequent user management.
/// </summary>
public class PlatformOwnerBootstrapService
{
    private readonly ILogger<PlatformOwnerBootstrapService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformOwnerBootstrapService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public PlatformOwnerBootstrapService(ILogger<PlatformOwnerBootstrapService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Assigns Platform Owner role to the first @maliev.com user if no owner exists.
    /// This method should be called during user authentication/registration.
    /// </summary>
    /// <param name="email">The user's email address.</param>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="assignRoleAsync">Function to assign a role to a user.</param>
    /// <param name="checkExistingOwnerAsync">Function to check if an owner exists for a role.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AssignPlatformOwnerIfNeededAsync(
        string email,
        string userId,
        Func<string, string, Task> assignRoleAsync,
        Func<string, Task<bool>> checkExistingOwnerAsync)
    {
        // Check if user is from @maliev.com domain
        if (!email.EndsWith("@maliev.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Check if ANY platform owner already exists
        var ownerExists = await checkExistingOwnerAsync(MalievIamRoles.PlatformOwner);
        if (ownerExists)
        {
            _logger.LogInformation("Platform owner already exists. Skipping assignment for {Email}", email);
            return;
        }

        // This is the first @maliev.com user - assign Platform Owner
        await assignRoleAsync(userId, MalievIamRoles.PlatformOwner);

        _logger.LogWarning(
            "PLATFORM OWNER ASSIGNED: User {Email} (ID: {UserId}) has been granted full platform control",
            email, userId);
    }
}
