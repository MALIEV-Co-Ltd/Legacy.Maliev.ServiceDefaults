using System.Reflection;

namespace Legacy.Maliev.ServiceDefaults.Tests;

public sealed class ApiDocumentationCompatibilityTests
{
    [Fact]
    public void OpenApiRegistrationUsesVersionAwareIntegration()
    {
        var source = ReadSource("Extensions.ApiDocumentation.cs");

        Assert.Contains(".AddApiVersioning()", source, StringComparison.Ordinal);
        Assert.Contains(".AddOpenApi(options =>", source, StringComparison.Ordinal);
        Assert.Contains("options.Document.AddDocumentTransformer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenApiEndpointMapsOneDocumentPerApiVersion()
    {
        var source = ReadSource("Extensions.ApiDocumentation.cs");

        Assert.Contains("/openapi/{documentName}.json", source, StringComparison.Ordinal);
        Assert.Contains(".WithDocumentPerVersion()", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", ".."));

        return File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Legacy.Maliev.ServiceDefaults",
            fileName));
    }
}
