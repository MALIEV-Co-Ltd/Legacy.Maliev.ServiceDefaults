using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Observes only the selected client's terminal outcome without exporting protected transport details.</summary>
internal sealed class PrivateDependencyFailureHandler(
    ILogger<PrivateDependencyFailureHandler> logger, string dependency, bool observeOperation, bool observeAllFailures = false)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = PrivateDependencyObservationContext.Get(request);
        // This callback runs only for the outer HttpClient deadline wrapper proven by the rendezvous.
        context?.Bind(() => RecordCore(request, null, observeAllFailures ? nameof(TaskCanceledException) : null));
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (observeAllFailures ? !response.IsSuccessStatusCode : (int)response.StatusCode is >= 500 and <= 599)
            {
                Record(request, (int)response.StatusCode);
            }
            return response;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            context?.CaptureTerminalCancellation(exception);
            throw;
        }
        catch (HttpRequestException exception)
        {
            Record(request, exception.StatusCode is { } status ? (int)status : null, exception);
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            Record(request, null, exception);
            throw;
        }
        catch (Exception exception) when (observeAllFailures)
        {
            Record(request, null, exception);
            throw;
        }
    }

    private void Record(HttpRequestMessage request, int? status, Exception? exception = null)
    {
        var exceptionType = observeAllFailures && exception is not null
            ? PrivateDependencyFailureMetadata.GetExceptionType(exception) : null;
        if (PrivateDependencyObservationContext.Get(request) is { } context)
            context.RecordOnce(() => RecordCore(request, status, exceptionType));
        else RecordCore(request, status, exceptionType);
    }

    private void RecordCore(HttpRequestMessage request, int? status, string? exceptionType = null)
    {
        try
        {
            var operation = observeOperation ? PrivateDependencyOperationMapper.GetOperation(request) : "HttpRequest";
            // Never pass the caught exception to ILogger: other providers need the same safe event boundary.
            if (observeAllFailures)
            {
                var method = PrivateDependencyFailureMetadata.GetMethod(request);
                if (status is { } sourceStatus)
                {
                    logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                        "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode} Method={Method} ExceptionType={ExceptionType}",
                        "DependencyRequestFailure", dependency, operation, sourceStatus, method, exceptionType);
                }
                else
                {
                    logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                        "{EventName} Dependency={Dependency} Operation={Operation} Method={Method} ExceptionType={ExceptionType}",
                        "DependencyRequestFailure", dependency, operation, method, exceptionType);
                }
                return;
            }
            if (status is { } knownStatus)
            {
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode}",
                    "DependencyRequestFailure", dependency, operation, knownStatus);
            }
            else
            {
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation}",
                    "DependencyRequestFailure", dependency, operation);
            }
        }
        catch (Exception)
        {
            // Best-effort observation only: provider failure must not replace the caller's outcome.
            // Do not log this failure recursively or disclose its potentially protected details.
        }
    }
}
