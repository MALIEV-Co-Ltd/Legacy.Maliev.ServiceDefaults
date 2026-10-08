using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maliev.Aspire.ServiceDefaults.LegacyAuth;

/// <summary>Supplies the separately enrolled, server-owned IAM service profile.</summary>
public interface ILegacyIamServiceAccessTokenProvider
{
    /// <summary>Gets an IAM-profile token, or null without falling back to legacy or locally signed tokens.</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    /// <summary>Invalidates only this provider's rejected IAM-profile token.</summary>
    void Invalidate(string token);
}

/// <summary>Uses a separate exchange and cache for the IAM profile, with the existing bounded credential protocol.</summary>
public sealed class LegacyIamServiceAccessTokenProvider : ILegacyIamServiceAccessTokenProvider
{
    private readonly LegacyServiceAccessTokenProvider inner;
    /// <summary>Initializes the opt-in IAM service-profile exchange.</summary>
    public LegacyIamServiceAccessTokenProvider(IHttpClientFactory clientFactory,
        IOptions<LegacyServiceAuthenticationOptions> options, TimeProvider timeProvider,
        ILogger<LegacyServiceAccessTokenProvider> logger)
    {
        inner = new LegacyServiceAccessTokenProvider(clientFactory, options, timeProvider, logger, LegacyServiceTokenProfile.Iam);
    }
    /// <inheritdoc />
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => inner.GetAccessTokenAsync(cancellationToken);
    /// <inheritdoc />
    public void Invalidate(string token) => inner.Invalidate(token);
}
