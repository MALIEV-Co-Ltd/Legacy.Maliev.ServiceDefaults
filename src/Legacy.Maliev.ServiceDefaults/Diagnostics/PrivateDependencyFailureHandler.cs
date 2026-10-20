using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Observes only the selected client's terminal outcome without exporting protected transport details.</summary>
internal sealed class PrivateDependencyFailureHandler(
    ILogger<PrivateDependencyFailureHandler> logger, string dependency, bool observeOperation, bool observeAllFailures = false)
    : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<bool> CallerFailureLogging = new("Maliev.DependencyFailureOwnedByCaller");

    private static bool CallerOwnsFailure(HttpRequestMessage request) =>
        request.Options.TryGetValue(CallerFailureLogging, out var owned) && owned;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = PrivateDependencyObservationContext.Get(request);
        // This callback runs only for the outer HttpClient deadline wrapper proven by the rendezvous.
        context?.Bind(() => RecordCore(request, null, observeAllFailures ? typeof(TaskCanceledException) : null));
        var bufferSourceResponse = observeAllFailures && context?.BindBufferedSource(exception => RecordCore(request, null,
            exception.GetType())) == true;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (bufferSourceResponse)
            {
                var status = (int)response.StatusCode;
                context!.CaptureBufferedResponse(response.IsSuccessStatusCode ? null : () => RecordCore(request, status));
            }
            else if (observeAllFailures ? !response.IsSuccessStatusCode : (int)response.StatusCode is >= 500 and <= 599)
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
        if (CallerOwnsFailure(request)) return;
        var exceptionType = observeAllFailures && exception is not null
            ? exception.GetType() : null;
        if (PrivateDependencyObservationContext.Get(request) is { } context)
            context.RecordOnce(() => RecordCore(request, status, exceptionType));
        else RecordCore(request, status, exceptionType);
    }

    private void RecordCore(HttpRequestMessage request, int? status, Type? exceptionType = null)
    {
        if (CallerOwnsFailure(request)) return;
        try
        {
            var operation = observeOperation ? PrivateDependencyOperationMapper.GetOperation(request) : "HttpRequest";
            // Never pass the caught exception to ILogger: other providers need the same safe event boundary.
            if (observeAllFailures)
            {
                var method = PrivateDependencyFailureMetadata.GetMethod(request);
                if (status == 404 && exceptionType is null)
                {
                    logger.LogDebug(new EventId(5101, "DependencyResourceNotFound"),
                        "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode} Method={Method}",
                        "DependencyResourceNotFound", dependency, operation, 404, method);
                    return;
                }
                var state = new PrivateDependencyFailureMetadata.SourceFailureState(dependency, operation, status, method, exceptionType);
                logger.Log(LogLevel.Error, new EventId(5101, "DependencyRequestFailure"), state, null,
                    static (source, _) => source.ToString());
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
