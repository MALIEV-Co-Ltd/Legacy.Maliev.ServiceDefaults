using System.Net.Http.Headers;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// Adds a centrally issued AuthService bearer token to an opt-in outbound HTTP client.
/// </summary>
public sealed class AuthServiceTokenExchangeHandler(IAuthServiceTokenProvider tokenProvider) : DelegatingHandler
{
    private readonly IAuthServiceTokenProvider _tokenProvider = tokenProvider
        ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
