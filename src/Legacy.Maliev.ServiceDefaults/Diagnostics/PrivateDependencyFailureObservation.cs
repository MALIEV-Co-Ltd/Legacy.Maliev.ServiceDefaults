namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Retains only whether the selected observer claimed one caller-owned send's failure.</summary>
/// <remarks>
/// Create a fresh instance for each send. A claimed observation remains true after the send unwinds,
/// including when a logging provider fails. This is an ownership signal, not a delivery receipt.
/// </remarks>
public sealed class PrivateDependencyFailureObservation
{
    private int started;
    private int observed;

    /// <summary>Gets whether the selected dependency observer claimed the failure before invoking its sink.</summary>
    public bool WasObserved => Volatile.Read(ref observed) != 0;

    internal void Begin()
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            throw new InvalidOperationException("Dependency observation state already belongs to a send.");
    }

    internal void MarkObserved() => Volatile.Write(ref observed, 1);
}
