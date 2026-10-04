using System.Security.Claims;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class IamRoleAuthorizationContractTests
{
    [Fact]
    public void PublicRoleConstants_RetainOriginalPersistedIdentifiers()
    {
        Assert.Equal("roles.platform.owner", MalievIamRoles.PlatformOwner);
        Assert.Equal("roles/maliev.serviceAdmin", MalievIamRoles.ServiceAdmin);
        Assert.Equal("roles/maliev.serviceViewer", MalievIamRoles.ServiceViewer);
        Assert.Equal("roles/maliev.serviceEditor", MalievIamRoles.ServiceEditor);
        Assert.Equal("roles/maliev.customer.admin", MalievIamRoles.CustomerAdmin);
        Assert.Equal("roles/maliev.customer.viewer", MalievIamRoles.CustomerViewer);
        Assert.Equal("roles/maliev.order.admin", MalievIamRoles.OrderAdmin);
        Assert.Equal("roles/maliev.order.viewer", MalievIamRoles.OrderViewer);
        Assert.Equal("roles/maliev.system.serviceAccount", MalievIamRoles.SystemServiceAccount);
    }

    [Theory]
    [InlineData("maliev.iam.role", "roles/maliev.serviceViewer", true)]
    [InlineData("maliev.iam.role", "roles/maliev.serviceEditor", true)]
    [InlineData("maliev.iam.role", "roles/maliev.serviceAdmin", false)]
    [InlineData("gcp.iam.role", "roles/maliev.serviceViewer", false)]
    [InlineData(ClaimTypes.Role, "roles/maliev.serviceEditor", false)]
    [InlineData("maliev.iam.role", "roles/maliev.ServiceViewer", false)]
    [InlineData("MALIEV.iam.role", "roles/maliev.serviceViewer", false)]
    public async Task RegisteredAuthorization_RequiresAnyExactMalievRoleNotAnotherClaimNamespace(string claimType, string role, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, MalievIamRoleHandler>();
        using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, role)], "fixture"));
        var requirement = new MalievIamRoleRequirement(MalievIamRoles.ServiceViewer, MalievIamRoles.ServiceEditor);

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, [requirement]);

        Assert.Equal(allowed, result.Succeeded);
        if (!allowed)
            Assert.Contains(requirement, result.Failure!.FailedRequirements);
    }

    [Fact]
    public async Task EmptyRequiredRoles_DoNotGrantAnOwnerImplicitBypass()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, MalievIamRoleHandler>();
        using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("maliev.iam.role", "roles.platform.owner")], "fixture"));

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, [new MalievIamRoleRequirement()]);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PublicRoleHelpers_SelectOnlyExactNamespaceAndOwnerValue()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("gcp.iam.role", "roles.platform.owner"),
            new Claim(ClaimTypes.Role, "roles.platform.owner"),
            new Claim("maliev.iam.role", "roles.platform.Owner"),
            new Claim("maliev.iam.role", "roles/maliev.serviceViewer"),
            new Claim("maliev.iam.role", "roles/maliev.serviceViewer")]));

        Assert.False(MalievIamRoles.IsPlatformOwner(user));
        Assert.Equal(new[] { "roles.platform.Owner", "roles/maliev.serviceViewer", "roles/maliev.serviceViewer" }, MalievIamRoles.GetUserRoles(user));
        user.AddIdentity(new ClaimsIdentity([new Claim("maliev.iam.role", "roles.platform.owner")]));
        Assert.True(MalievIamRoles.IsPlatformOwner(user));
    }

    [Theory]
    [InlineData("fixture@example.test", false, 0, 0)]
    [InlineData("fixture@maliev.com.example.test", false, 0, 0)]
    [InlineData("fixture@maliev.com", true, 1, 0)]
    [InlineData("fixture@maliev.com", false, 1, 1)]
    [InlineData("fixture@MALIEV.COM", false, 1, 1)]
    public async Task Bootstrap_RespectsDomainAndExistingOwnerBeforeExactAssignment(string email, bool ownerExists, int expectedChecks, int expectedAssignments)
    {
        var checkedRoles = new List<string>();
        var assigned = new List<(string User, string Role)>();
        var service = new PlatformOwnerBootstrapService(NullLogger<PlatformOwnerBootstrapService>.Instance);

        await service.AssignPlatformOwnerIfNeededAsync(email, "synthetic-bootstrap-user",
            (user, role) => { assigned.Add((user, role)); return Task.CompletedTask; },
            role => { checkedRoles.Add(role); return Task.FromResult(ownerExists); });

        Assert.Equal(expectedChecks, checkedRoles.Count);
        Assert.All(checkedRoles, role => Assert.Equal("roles.platform.owner", role));
        Assert.Equal(expectedAssignments, assigned.Count);
        Assert.All(assigned, value =>
        {
            Assert.Equal("synthetic-bootstrap-user", value.User);
            Assert.Equal("roles.platform.owner", value.Role);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_CallbackFailurePropagatesWithoutClaimingAssignment(bool assignmentFailure)
    {
        var checks = 0;
        var assignments = 0;
        var failure = new InvalidOperationException("synthetic bootstrap callback failure");
        var service = new PlatformOwnerBootstrapService(NullLogger<PlatformOwnerBootstrapService>.Instance);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignPlatformOwnerIfNeededAsync(
            "fixture@maliev.com", "synthetic-bootstrap-user",
            (_, _) => { assignments++; return Task.FromException(failure); },
            _ => { checks++; return assignmentFailure ? Task.FromResult(false) : Task.FromException<bool>(failure); }));

        Assert.Same(failure, actual);
        Assert.Equal(1, checks);
        Assert.Equal(assignmentFailure ? 1 : 0, assignments);
    }
}
