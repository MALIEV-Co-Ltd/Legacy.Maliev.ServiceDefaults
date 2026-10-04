using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Maliev.Aspire.ServiceDefaults.Logging;

/// <summary>Formats explicitly selected failure console output without arbitrary text or state.</summary>
public sealed class PrivateFailureConsoleFormatter : ConsoleFormatter
{
    /// <summary>Gets the opt-in formatter name; existing console defaults are unchanged.</summary>
    public const string FormatterName = "maliev-private-failure-json";

    /// <summary>Initializes the explicitly selected private failure formatter.</summary>
    public PrivateFailureConsoleFormatter() : base(FormatterName) { }

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        if (logEntry.LogLevel is not (LogLevel.Warning or LogLevel.Error or LogLevel.Critical))
        {
            return;
        }

        var assembly = Assembly.GetEntryAssembly();
        var failure = logEntry.Exception;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = logEntry.LogLevel switch
            {
                LogLevel.Warning => "WARNING",
                LogLevel.Error => "ERROR",
                _ => "CRITICAL"
            },
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["logger"] = IsCodeIdentifier(logEntry.Category) ? logEntry.Category : "ConfiguredLogger",
            ["eventId"] = logEntry.EventId.Id,
            ["message"] = "Application diagnostic; see event and exception metadata",
            ["service"] = SafeServiceName(assembly?.GetName().Name),
            ["deploymentVersion"] = SafeDeploymentVersion(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion),
            ["exceptionType"] = SafeTypeName(failure?.GetType().FullName),
            ["innerExceptionType"] = SafeTypeName(failure?.InnerException?.GetType().FullName),
            ["sourceLocation"] = SafeSourceLocation(failure?.TargetSite)
        };
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
            && activity.TraceId != default && activity.SpanId != default)
        {
            payload.Add("traceId", activity.TraceId.ToHexString());
            payload.Add("spanId", activity.SpanId.ToHexString());
        }
        AddSafeFields(payload, logEntry.State);
        if (scopeProvider is not null)
        {
            var stop = new ScopeBudgetReachedException();
            int scopeCount = 0;
            try
            {
                scopeProvider.ForEachScope((scope, fields) =>
                {
                    AddSafeFields(fields, scope);
                    if (++scopeCount == 16)
                    {
                        throw stop;
                    }
                }, payload);
            }
            catch (ScopeBudgetReachedException exception) when (ReferenceEquals(exception, stop))
            {
                // Stop only this traversal's own budget marker; provider failures propagate.
            }
        }
        textWriter.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static void AddSafeFields(Dictionary<string, object?> payload, object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        bool synthetic = false;
        string? diagnosticId = null;
        foreach (var field in fields.Take(64))
        {
            if (field.Key == "Synthetic" && field.Value is true) synthetic = true;
            else if (field.Key == "DiagnosticId" && field.Value is string nonce
                && Guid.TryParseExact(nonce, "N", out var parsed)) diagnosticId = parsed.ToString("N");
            if (payload.ContainsKey(field.Key))
            {
                continue;
            }

            if (field.Key is "StatusCode" or "ElapsedMs" or "AttemptCount" && field.Value is int or long)
            {
                payload.Add(field.Key, field.Value);
            }
            else if (field.Key is "EventName" or "Dependency" or "Operation" or "Method"
                && field.Value is string identifier && IsCodeIdentifier(identifier))
            {
                payload.Add(field.Key, identifier);
            }
        }
        if (synthetic && diagnosticId is not null && !payload.ContainsKey("Synthetic"))
        {
            payload.Add("Synthetic", true);
            payload.Add("DiagnosticId", diagnosticId);
        }
    }

    private static bool IsCodeIdentifier(string value) => value.Length is >= 1 and <= 96
        && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    // Runtime provenance is never replaced with caller state or arbitrary exception text.
    // Missing, oversized or non-identifier metadata fails closed rather than being truncated.
    private static string? SafeServiceName(string? name) => name is not null && IsCodeIdentifier(name) ? name : null;

    private static string? SafeDeploymentVersion(string? version) => version is { Length: >= 1 and <= 256 }
        && char.IsAsciiLetterOrDigit(version[0])
        && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' or '+')
            ? version : null;

    private static string? SafeTypeName(string? name) => name is { Length: >= 1 and <= 256 }
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '+' or '`')
            ? name : null;

    private static string? SafeSourceLocation(MethodBase? targetSite)
    {
        var typeName = SafeTypeName(targetSite?.DeclaringType?.FullName);
        var methodName = targetSite?.Name;
        return typeName is not null && methodName is { Length: >= 1 and <= 96 }
            && (char.IsAsciiLetter(methodName[0]) || methodName[0] == '_')
            && methodName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? typeName + "." + methodName : null;
    }

    private sealed class ScopeBudgetReachedException : Exception;
}
