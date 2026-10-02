using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Observes only the selected client's terminal outcome without exporting protected transport details.</summary>
internal sealed class PrivateDependencyFailureHandler(ILogger<PrivateDependencyFailureHandler> logger, string dependency)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 500 and <= 599)
            {
                Record((int)response.StatusCode);
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            Record(exception.StatusCode is { } status ? (int)status : null);
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Record(null);
            throw;
        }
    }

    private void Record(int? status)
    {
        try
        {
            // Never pass the caught exception to ILogger: other providers need the same safe event boundary.
            if (status is { } knownStatus)
            {
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode}",
                    "DependencyRequestFailure", dependency, "HttpRequest", knownStatus);
            }
            else
            {
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation}",
                    "DependencyRequestFailure", dependency, "HttpRequest");
            }
        }
        catch (Exception)
        {
            // Best-effort observation only: provider failure must not replace the caller's outcome.
            // Do not log this failure recursively or disclose its potentially protected details.
        }
    }
}
