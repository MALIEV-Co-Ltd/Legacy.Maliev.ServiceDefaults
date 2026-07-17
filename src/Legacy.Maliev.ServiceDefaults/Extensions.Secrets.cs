using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for loading secrets from various sources.
/// </summary>
public static class SecretsExtensions
{
    /// <summary>
    /// Loads secrets from Google Secret Manager mounted as a Kubernetes volume.
    /// Typically mounted at /mnt/secrets with each secret as a separate file.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="secretsPath">The path where secrets are mounted (default: /mnt/secrets).</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddGoogleSecretManagerVolume(
        this IHostApplicationBuilder builder,
        string secretsPath = "/mnt/secrets")
    {
        if (Directory.Exists(secretsPath))
        {
            builder.Configuration.AddKeyPerFile(directoryPath: secretsPath, optional: true);
        }

        return builder;
    }
}
