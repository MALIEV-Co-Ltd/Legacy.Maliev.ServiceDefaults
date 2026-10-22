namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

// Method tokens stay bounded; custom exception names require source-owned state.
internal static class PrivateDependencyFailureMetadata
{
    internal static string GetMethod(HttpRequestMessage request) => request.Method.Method switch
    {
        "GET" => "GET",
        "HEAD" => "HEAD",
        "POST" => "POST",
        "PUT" => "PUT",
        "DELETE" => "DELETE",
        "CONNECT" => "CONNECT",
        "OPTIONS" => "OPTIONS",
        "TRACE" => "TRACE",
        "PATCH" => "PATCH",
        _ => "UnknownMethod"
    };

    internal static bool IsExceptionType(string value) => value is nameof(HttpRequestException)
        or nameof(TimeoutException) or nameof(OperationCanceledException) or nameof(TaskCanceledException)
        or nameof(Polly.Timeout.TimeoutRejectedException) or nameof(IOException) or nameof(InvalidOperationException)
        or nameof(ArgumentException) or nameof(NotSupportedException) or "UnknownException";

    internal sealed class SourceFailureState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] fields;
        private readonly string message;

        internal SourceFailureState(string dependency, string operation, int? status, string method, Type? exceptionType)
        {
            var name = exceptionType?.Name;
            const string prefix = "{EventName} Dependency={Dependency} Operation={Operation}";
            const string suffix = " Method={Method} ExceptionType={ExceptionType}";
            var format = prefix + (status is null ? string.Empty : " StatusCode={StatusCode}") + suffix;
            fields = status is { } code
                ? [new("EventName", "DependencyRequestFailure"), new("Dependency", dependency), new("Operation", operation),
                    new("StatusCode", code), new("Method", method), new("ExceptionType", name), new("{OriginalFormat}", format)]
                : [new("EventName", "DependencyRequestFailure"), new("Dependency", dependency), new("Operation", operation),
                    new("Method", method), new("ExceptionType", name), new("{OriginalFormat}", format)];
            var formattedName = name ?? "(null)";
            message = status is { } messageStatus
                ? FormattableString.Invariant($"DependencyRequestFailure Dependency={dependency} Operation={operation} StatusCode={messageStatus} Method={method} ExceptionType={formattedName}")
                : FormattableString.Invariant($"DependencyRequestFailure Dependency={dependency} Operation={operation} Method={method} ExceptionType={formattedName}");
        }

        public int Count => fields.Length;
        public KeyValuePair<string, object?> this[int index] => fields[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => message;
    }
}
