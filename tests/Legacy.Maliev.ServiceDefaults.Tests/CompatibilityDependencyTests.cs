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
