using System.Collections;
using System.Reflection;
using System.Security.Claims;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.ServiceDefaults.Tests.Unit;

public sealed class PermissionAuthorizationSemaphoreTests
{
    [Fact]
    public void Permission_handler_uses_a_reference_counted_static_semaphore_registry()
    {
        var field = typeof(PermissionAuthorizationHandler).GetField(
            "_permissionSemaphores",
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.Equal(typeof(Dictionary<,>), field.FieldType.GetGenericTypeDefinition());

        var registryLock = typeof(PermissionAuthorizationHandler).GetField(
            "_permissionSemaphoreRegistryLock",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(registryLock);
    }

    [Fact]
    public async Task Permission_handler_removes_registry_entry_after_the_last_waiter_leaves()
    {
        const string principalId = "cache-cleanup-regression-user";
        var configuration = new ServiceCollection().BuildServiceProvider();
        var httpContext = new DefaultHttpContext();
        var handler = new PermissionAuthorizationHandler(
            configuration,
            new HttpContextAccessor { HttpContext = httpContext },
            NullLogger<PermissionAuthorizationHandler>.Instance);
        var requirement = new PermissionRequirement("project.projects.read");
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", principalId),
                new Claim("permissions", requirement.Permission)
            ], "test")),
            httpContext);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        var field = typeof(PermissionAuthorizationHandler).GetField(
            "_permissionSemaphores",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var registry = Assert.IsAssignableFrom<IDictionary>(field!.GetValue(null));
        Assert.DoesNotContain(
            registry.Keys.Cast<object>(),
            key => string.Equals(
                key.ToString(),
                $"{principalId}:{requirement.Permission}:global:standard",
                StringComparison.Ordinal));
    }
}
