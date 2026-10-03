namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Identifies an admitted controlled diagnostic rather than a business failure.</summary>
public sealed class ProductionObservabilityDiagnosticException : Exception
{
    internal ProductionObservabilityDiagnosticException()
        : base("Controlled loopback observability diagnostic; no business operation executed.") { }
}
