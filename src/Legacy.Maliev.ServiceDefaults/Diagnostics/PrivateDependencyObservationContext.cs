namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

// A rendezvous owned only by one outer send, not by the pooled handler or any cancellation callback.
internal sealed class PrivateDependencyObservationContext(PrivateDependencyFailureObservation? observation = null)
{
    internal static readonly HttpRequestOptionsKey<PrivateDependencyObservationContext?> Key =
        new("Maliev.PrivateDependencyObservationContext");
    private Action? recordNativeDeadline;
    private OperationCanceledException? terminalCancellation;
    private int recorded;

    internal static PrivateDependencyObservationContext? Get(HttpRequestMessage request) =>
        request.Options.TryGetValue(Key, out var context) ? context : null;

    internal void Bind(Action record) => recordNativeDeadline = record;

    internal void CaptureTerminalCancellation(OperationCanceledException exception) => terminalCancellation = exception;

    internal void RecordOnce(Action record)
    {
        if (Interlocked.Exchange(ref recorded, 1) == 0)
        {
            observation?.MarkObserved();
            record();
        }
    }

    internal void TryRecordNativeDeadline(OperationCanceledException exception, CancellationToken callerToken)
    {
        // HttpClient.HandleFailure creates this fresh wrapper only after checking its actual captured
        // pending-request CTS and original caller token. Shape alone is insufficient: the innermost
        // exception must be the exact terminal OCE rethrown by the selected outer handler.
        // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs#L529-L538
        if (!callerToken.IsCancellationRequested && terminalCancellation is { } original
            && !ReferenceEquals(exception, original)
            && exception is TaskCanceledException { InnerException: TimeoutException timeout }
            && ReferenceEquals(timeout.InnerException, original)
            && recordNativeDeadline is { } record)
            RecordOnce(record);
    }

    internal void Clear()
    {
        terminalCancellation = null;
        recordNativeDeadline = null;
    }
}
