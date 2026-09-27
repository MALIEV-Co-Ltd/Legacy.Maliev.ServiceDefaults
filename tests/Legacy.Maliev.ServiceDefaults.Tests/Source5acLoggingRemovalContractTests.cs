using System.Reflection;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Hosting;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class Source5acLoggingRemovalContractTests
{
    [Fact]
    public void SharedDefaults_DoNotDependOnRetiredNativeLoggingAssemblyOrSourceApi()
    {
        var assembly = typeof(Extensions).Assembly;
        Assert.Equal("Legacy.Maliev.ServiceDefaults", assembly.GetName().Name);
        Assert.Equal(assembly, typeof(MalievCloudJsonConsoleFormatter).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Maliev.NativeLogging" or "Maliev.LoggerService.NLog");

        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "Legacy.Maliev.ServiceDefaults",
            "Legacy.Maliev.ServiceDefaults.csproj"));
        Assert.DoesNotContain("Maliev.NativeLogging", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.LoggerService", project, StringComparison.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(root, "src", "Legacy.Maliev.ServiceDefaults"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("using Maliev.NativeLogging", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AddMalievJsonConsole", source, StringComparison.Ordinal);
            Assert.DoesNotContain("UseMalievProductionExceptionHandler", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.ServiceDefaults.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("ServiceDefaults root was not found.");
    }
}
