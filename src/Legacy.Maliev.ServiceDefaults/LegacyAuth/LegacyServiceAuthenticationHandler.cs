using System.Net;
using System.Net.Http.Headers;

namespace Maliev.Aspire.ServiceDefaults.LegacyAuth;

/// <summary>Adds a legacy AuthService workload token to an opt-in outbound HTTP client.</summary>
public sealed class LegacyServiceAuthenticationHandler(ILegacyServiceAccessTokenProvider tokenProvider) : DelegatingHandler
{
    private readonly ILegacyServiceAccessTokenProvider tokenProvider = tokenProvider
        ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new HttpRequestException("Legacy service authentication is unavailable.", null, HttpStatusCode.ServiceUnavailable);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            tokenProvider.Invalidate(token);
        return response;
    }
}
