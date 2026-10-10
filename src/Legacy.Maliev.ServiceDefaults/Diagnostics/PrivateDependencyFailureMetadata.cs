namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

// Source metadata adaptation: never export arbitrary method tokens or application-defined type names.
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

    internal static string GetExceptionType(Exception exception)
    {
        var type = exception.GetType();
        if (type == typeof(HttpRequestException)) return nameof(HttpRequestException);
        if (type == typeof(TimeoutException)) return nameof(TimeoutException);
        if (type == typeof(OperationCanceledException)) return nameof(OperationCanceledException);
        if (type == typeof(TaskCanceledException)) return nameof(TaskCanceledException);
        if (type == typeof(Polly.Timeout.TimeoutRejectedException)) return nameof(Polly.Timeout.TimeoutRejectedException);
        if (type == typeof(IOException)) return nameof(IOException);
        if (type == typeof(InvalidOperationException)) return nameof(InvalidOperationException);
        if (type == typeof(ArgumentException)) return nameof(ArgumentException);
        if (type == typeof(NotSupportedException)) return nameof(NotSupportedException);
        return "UnknownException";
    }

    internal static bool IsExceptionType(string value) => value is nameof(HttpRequestException)
        or nameof(TimeoutException) or nameof(OperationCanceledException) or nameof(TaskCanceledException)
        or nameof(Polly.Timeout.TimeoutRejectedException) or nameof(IOException) or nameof(InvalidOperationException)
        or nameof(ArgumentException) or nameof(NotSupportedException) or "UnknownException";
}
