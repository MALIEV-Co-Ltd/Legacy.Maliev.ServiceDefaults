namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class DatabaseMigrationBoundaryTests
{
    [Fact]
    public void ServiceDefaults_DoesNotApplyMigrationsFromAnApplicationProcess()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Legacy.Maliev.ServiceDefaults",
            "Extensions.Database.cs"));
        var methodStart = source.IndexOf("public static Task MigrateDatabaseAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("    private static string EnsureConnectionPooling", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "Expected the compatibility migration method.");
        Assert.True(methodEnd > methodStart, "Expected the migration method to end before pooling helpers.");

        var method = source[methodStart..methodEnd];
        Assert.Contains("Implicit service-startup database migration is disabled", method, StringComparison.Ordinal);
        Assert.Contains("Legacy.Maliev.AppHost MigrationRunner", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Database.MigrateAsync", method, StringComparison.Ordinal);
        Assert.Contains("LEGACY001", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ServiceDefaults.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
