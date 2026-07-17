using System.Reflection;
using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.Database;
using Maliev.Aspire.ServiceDefaults.IAM;
using HostingExtensions = Microsoft.Extensions.Hosting.Extensions;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class CompatibilityBaselineTests
{
    [Theory]
    [InlineData("legacy-orders.orders.read", "legacy-orders.orders.read", true)]
    [InlineData("legacy-orders.orders.read", "legacy-orders.orders.*", true)]
    [InlineData("legacy-orders.orders.read", "legacy-orders.orders.create", false)]
    public void PermissionMatchingPreservesLegacySemantics(string required, string granted, bool expected)
    {
        Assert.Equal(expected, PermissionMatcher.IsMatch(required, granted));
    }

    [Theory]
    [InlineData("OrderLineID", "order_line_id")]
    [InlineData("GCSObjectPath", "gcs_object_path")]
    [InlineData("CreatedAt", "created_at")]
    public void SnakeCaseConversionPreservesLegacyDatabaseNames(string input, string expected)
    {
        Assert.Equal(expected, SnakeCaseNamingHelper.ToSnakeCase(input));
    }

    [Fact]
    public void CoreHostingExtensionSurfaceRemainsAvailable()
    {
        var methods = typeof(HostingExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("AddServiceDefaults", methods);
        Assert.Contains("AddServiceMeters", methods);
        Assert.Contains("MapDefaultEndpoints", methods);
    }
}
