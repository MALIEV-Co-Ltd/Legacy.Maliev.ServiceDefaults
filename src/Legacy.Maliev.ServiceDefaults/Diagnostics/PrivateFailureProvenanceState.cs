using System.Collections;
using System.Globalization;
using Maliev.Aspire.ServiceDefaults.Logging;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

// Only these producer factories can attach runtime provenance. Enumeration exposes the
// existing scalar event contract, never an Exception, MethodBase, stack or caller state.
internal sealed class PrivateFailureProvenanceState : IReadOnlyList<KeyValuePair<string, object?>>
{
    internal sealed record Snapshot(string? ExceptionType, string? InnerExceptionType, string? SourceLocation);

    private readonly KeyValuePair<string, object?>[] fields;
    private readonly string message;
    internal Snapshot Provenance { get; }

    private PrivateFailureProvenanceState(Snapshot provenance, string template, string format,
        string[] names, object?[] values)
    {
        Provenance = provenance;
        fields = new KeyValuePair<string, object?>[names.Length + 1];
        for (int index = 0; index < names.Length; index++)
        {
            fields[index] = new(names[index], values[index]);
        }
        fields[^1] = new("{OriginalFormat}", template);
        message = string.Format(CultureInfo.InvariantCulture, format, values);
    }

    internal static Snapshot Capture(Exception exception)
    {
        try
        {
            return new(
                PrivateFailureConsoleFormatter.SafeTypeName(exception.GetType().FullName),
                PrivateFailureConsoleFormatter.SafeTypeName(exception.InnerException?.GetType().FullName),
                PrivateFailureConsoleFormatter.SafeSourceLocation(exception.TargetSite));
        }
        catch (Exception)
        {
            // Metadata failure must not replace the original request or transport outcome.
            return new(null, null, null);
        }
    }

    internal static PrivateFailureProvenanceState ForDependency(string dependency, string operation,
        int? status, Snapshot provenance) => status is { } known
        ? new(provenance,
            "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode}",
            "{0} Dependency={1} Operation={2} StatusCode={3}",
            ["EventName", "Dependency", "Operation", "StatusCode"],
            ["DependencyRequestFailure", dependency, operation, known])
        : new(provenance,
            "{EventName} Dependency={Dependency} Operation={Operation}",
            "{0} Dependency={1} Operation={2}",
            ["EventName", "Dependency", "Operation"],
            ["DependencyRequestFailure", dependency, operation]);

    internal static PrivateFailureProvenanceState ForRequest(string service, string method, string path,
        int status, string exceptionType, string incidentId, string occurredAtUtc, Snapshot provenance) =>
        new(provenance,
            "{EventName} Service={Service} Method={Method} Path={Path} StatusCode={StatusCode} ExceptionType={ExceptionType} IncidentId={IncidentId} OccurredAtUtc={OccurredAtUtc}",
            "{0} Service={1} Method={2} Path={3} StatusCode={4} ExceptionType={5} IncidentId={6} OccurredAtUtc={7}",
            ["EventName", "Service", "Method", "Path", "StatusCode", "ExceptionType", "IncidentId", "OccurredAtUtc"],
            ["UnhandledRequestFailure", service, method, path, status, exceptionType, incidentId, occurredAtUtc]);

    public int Count => fields.Length;
    public KeyValuePair<string, object?> this[int index] => fields[index];
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => fields.GetEnumerator();
    public override string ToString() => message;
}
