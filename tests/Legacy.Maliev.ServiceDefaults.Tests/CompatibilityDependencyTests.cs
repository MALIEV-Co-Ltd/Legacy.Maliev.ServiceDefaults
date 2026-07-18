namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class CompatibilityDependencyTests
{
    [Fact]
    public void JwtBearerAndTokenHandlerUseCompatiblePackageVersions()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "Legacy.Maliev.ServiceDefaults.csproj"));

        Assert.Contains(
            "PackageReference Include=\"Microsoft.AspNetCore.Authentication.JwtBearer\" Version=\"10.0.10\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "PackageReference Include=\"System.IdentityModel.Tokens.Jwt\" Version=\"8.19.2\"",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectAndCiUseOnlyLegacyCompatibilityContracts()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "Legacy.Maliev.ServiceDefaults.csproj"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "_build-and-test.yml"));

        Assert.Contains("Legacy.Maliev.CompatibilityContracts", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.MessagingContracts.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PackageReference Include=\"Maliev.MessagingContracts\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: c96b6bf76e0f42696de2f4728a788568cdf41d47", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("repository: MALIEV-Co-Ltd/Maliev.MessagingContracts", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DependabotDefersMicrosoftOpenApiMajorUpdatesUntilAspNetCoreIsCompatible()
    {
        var root = FindRepositoryRoot();
        var dependabot = File.ReadAllText(Path.Combine(root, ".github", "dependabot.yml"));

        Assert.Contains("dependency-name: Microsoft.OpenApi", dependabot, StringComparison.Ordinal);
        Assert.Contains("version-update:semver-major", dependabot, StringComparison.Ordinal);
    }

    [Fact]
    public void DependabotGroupsOnlyCompatibleGitHubActionsVersionUpdates()
    {
        var block = ReadDependabotUpdateBlock("github-actions");

        Assert.Contains("    open-pull-requests-limit: 5", block);
        Assert.Contains("    groups:", block);
        Assert.Contains("      minor-and-patch:", block);
        Assert.Contains("        applies-to: version-updates", block);
        Assert.Contains("        patterns:", block);
        Assert.Contains("          - \"*\"", block);
        Assert.Contains("        update-types:", block);
        Assert.Contains("          - minor", block);
        Assert.Contains("          - patch", block);
        Assert.DoesNotContain(block, line => line.Contains("major", StringComparison.Ordinal));
    }

    private static string[] ReadDependabotUpdateBlock(string packageEcosystem)
    {
        var root = FindRepositoryRoot();
        var lines = File.ReadAllLines(Path.Combine(root, ".github", "dependabot.yml"));
        var blockStart = Array.FindIndex(
            lines,
            line => line.Trim() == $"- package-ecosystem: {packageEcosystem}");

        Assert.True(blockStart >= 0, $"Dependabot update block '{packageEcosystem}' was not found.");

        var blockEnd = Array.FindIndex(
            lines,
            blockStart + 1,
            line => line.StartsWith("  - package-ecosystem:", StringComparison.Ordinal));
        if (blockEnd < 0)
        {
            blockEnd = lines.Length;
        }

        return lines[blockStart..blockEnd]
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ServiceDefaults.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
