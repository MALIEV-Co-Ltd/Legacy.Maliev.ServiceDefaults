using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Maliev.Aspire.ServiceDefaults.Authorization;

/// <summary>
/// Authorization policy provider that dynamically creates policies from permission strings.
/// Supports parsing permission-based policy names with optional modifiers for model validation, 
/// critical operations, resource scoping, live checks, and audit purposes.
/// </summary>
public class PermissionAuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
{
    /// <summary>
    /// Initializes a new instance of the PermissionAuthorizationPolicyProvider.
    /// </summary>
    /// <param name="options">The authorization options configuration.</param>
    public PermissionAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options) : base(options)
    {
    }

    /// <summary>
    /// Creates an authorization policy from a permission-based policy name.
    /// Supports format: Permission:{permission}[:validate_model][:critical][:resource_{escaped-template}][:live_check][:purpose_{text}]
    /// </summary>
    /// <param name="policyName">The policy name to parse.</param>
    /// <returns>The authorization policy if the name starts with "Permission:", otherwise null.</returns>
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith("Permission:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Policy Format: Permission:{permission}[:validate_model][:critical][:resource_{escaped-template}][:live_check][:purpose_{text}]
                // Example: Permission:invoices.create:critical:purpose_Financial_Audit
                var content = policyName.Substring("Permission:".Length);

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new ArgumentException("Permission name cannot be empty", nameof(policyName));
                }

                var parts = content.Split(':');
                var permission = parts[0];

                bool validateModel = false;
                bool isCritical = false;
                string? resourcePathTemplate = null;
                bool requireLiveCheck = false;
                string? auditPurpose = null;

                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Equals("validate_model", StringComparison.OrdinalIgnoreCase))
                    {
                        validateModel = true;
                    }
                    else if (parts[i].Equals("critical", StringComparison.OrdinalIgnoreCase))
                    {
                        isCritical = true;
                    }
                    else if (parts[i].StartsWith("resource_", StringComparison.OrdinalIgnoreCase))
                    {
                        resourcePathTemplate = Uri.UnescapeDataString(parts[i]["resource_".Length..]);
                    }
                    else if (parts[i].Equals("live_check", StringComparison.OrdinalIgnoreCase))
                    {
                        requireLiveCheck = true;
                    }
                    else if (parts[i].StartsWith("purpose_", StringComparison.OrdinalIgnoreCase))
                    {
                        auditPurpose = parts[i].Substring("purpose_".Length).Replace('_', ' ');
                    }
                    // Ignore unknown parts for forward compatibility
                }

                var defaultPolicy = await GetDefaultPolicyAsync();
                return new AuthorizationPolicyBuilder(defaultPolicy)
                    .AddRequirements(new PermissionRequirement(
                        permission,
                        resourcePathTemplate: resourcePathTemplate,
                        requireLiveCheck: requireLiveCheck,
                        preValidateModel: validateModel,
                        isCritical: isCritical,
                        auditPurpose: auditPurpose))
                    .Build();
            }
            catch (Exception ex)
            {
                // Log error but don't crash - fall back to default policy provider
                // This prevents malformed policy names from breaking authorization
                throw new InvalidOperationException($"Failed to parse permission policy '{policyName}'. Format should be 'Permission:{{permission}}[:validate_model][:critical][:resource_{{escaped-template}}][:live_check][:purpose_{{text}}]'", ex);
            }
        }

        return await base.GetPolicyAsync(policyName);
    }
}
