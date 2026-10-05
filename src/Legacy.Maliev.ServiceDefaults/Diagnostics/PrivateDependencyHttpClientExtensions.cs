namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Observes selected dependency sends across the native HttpClient deadline boundary.</summary>
public static class PrivateDependencyHttpClientExtensions
{
    /// <summary>Sends through the real client with ResponseHeadersRead and observes a proven native deadline rewrite.</summary>
    /// <param name="client">The unchanged factory-owned HTTP client.</param>
    /// <param name="request">The caller-owned request, with observation state retained only during this send.</param>
    /// <param name="cancellationToken">The original caller cancellation token.</param>
    /// <returns>The original response returned by the client.</returns>
    /// <remarks>
    /// Requires explicit private failure selection to emit an event. Existing handlers retain ownership of
    /// transport failures and terminal responses. This does not observe later response-body reads or buffering.
    /// </remarks>
    public static Task<HttpResponseMessage> SendWithPrivateFailureObservationAsync(
        this HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        if (PrivateDependencyObservationContext.Get(request) is not null)
            throw new InvalidOperationException("Dependency observation already owns this request.");

        var context = new PrivateDependencyObservationContext();
        request.Options.Set(PrivateDependencyObservationContext.Key, context);
        try
        {
            // Preserve native synchronous request validation and the client's real pending CTS snapshot.
            var pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return ObserveAsync(pending, request, context, cancellationToken);
        }
        catch
        {
            Clear(request, context);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> ObserveAsync(Task<HttpResponseMessage> pending,
        HttpRequestMessage request, PrivateDependencyObservationContext context, CancellationToken callerToken)
    {
        try { return await pending.ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        {
            context.TryRecordNativeDeadline(exception, callerToken);
            throw;
        }
        finally { Clear(request, context); }
    }

    private static void Clear(HttpRequestMessage request, PrivateDependencyObservationContext context)
    {
        context.Clear();
        if (ReferenceEquals(PrivateDependencyObservationContext.Get(request), context))
            request.Options.Set(PrivateDependencyObservationContext.Key, null);
    }
}
