using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace Maliev.Aspire.ServiceDefaults.IAM;

/// <summary>
/// HTTP message handler that adds a service account JWT token to every outgoing request.
/// This handler always generates a service account token. Callers that need to forward
/// the authenticated user's token should use <c>UserContextHandler</c> or
/// <c>CookieForwardingHandler</c> instead.
/// </summary>
public class ServiceAccountAuthenticationHandler : DelegatingHandler
{
    private readonly IServiceAccountTokenProvider _tokenProvider;
    private readonly ILogger<ServiceAccountAuthenticationHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the ServiceAccountAuthenticationHandler with the specified dependencies.
    /// </summary>
    /// <param name="tokenProvider">The service account token provider.</param>
    /// <param name="logger">Logger for authentication operations.</param>
    public ServiceAccountAuthenticationHandler(
        IServiceAccountTokenProvider tokenProvider,
        ILogger<ServiceAccountAuthenticationHandler> logger)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Intercepts outgoing HTTP requests and adds a service account JWT token to the Authorization header.
    /// </summary>
    /// <param name="request">The HTTP request message.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The HTTP response message.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestTarget = SafeRequestTarget(request);
        _logger.LogDebug("ServiceAccountAuthenticationHandler invoked for {Method} {RequestTarget}", request.Method, requestTarget);

        // Defensive check: Ensure InnerHandler is set
        if (InnerHandler == null)
        {
            var error = "InnerHandler is null - handler not properly configured in HttpClient pipeline";
            _logger.LogError(error);
            throw new InvalidOperationException(error);
        }

        // Always generate a service account token. Callers that need to forward
        // the end-user's token should use UserContextHandler / CookieForwardingHandler
        // instead of this handler — mixing both would be incorrect.
        try
        {
            var token = _tokenProvider.GetToken();
            _logger.LogDebug("Generated fresh service account token for request to {RequestTarget}", requestTarget);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _logger.LogDebug("Authorization header set on request");

            return await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Request to {RequestTarget} was canceled during shutdown.", requestTarget);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("Connection to IAM service failed: {Message}. This is expected if the service is not yet available or in integration tests.", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ServiceAccountAuthenticationHandler.SendAsync for {RequestTarget}", requestTarget);
            throw;
        }
    }

    private static string SafeRequestTarget(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsolutePath is not { Length: > 0 } path)
        {
            return "(unknown)";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "/";
        }

        return "/" + string.Join('/', segments.Select(SanitizeSegment));
    }

    private static string SanitizeSegment(string segment)
    {
        if (long.TryParse(segment, out _) || Guid.TryParse(segment, out _))
        {
            return "{id}";
        }

        // Escaped path data can contain email addresses or other customer-provided values.
        // Opaque long segments are commonly hashes, operation IDs, or storage references.
        if (segment.Contains('%', StringComparison.Ordinal)
            || (segment.Length >= 24 && segment.All(static value => char.IsLetterOrDigit(value) || value is '-' or '_')))
        {
            return "{value}";
        }

        return segment;
    }

    /// <summary>
    /// Releases the resources used by the ServiceAccountAuthenticationHandler.
    /// </summary>
    /// <param name="disposing">True to release managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        // Don't dispose InnerHandler - it's managed by HttpClient factory
        base.Dispose(disposing);
    }
}
