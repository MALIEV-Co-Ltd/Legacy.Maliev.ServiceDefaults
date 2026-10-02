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

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = logEntry.LogLevel switch
            {
                LogLevel.Warning => "WARNING",
                LogLevel.Error => "ERROR",
                _ => "CRITICAL"
            },
            ["logger"] = IsCodeIdentifier(logEntry.Category) ? logEntry.Category : "ConfiguredLogger",
            ["eventId"] = logEntry.EventId.Id,
            ["message"] = "Application diagnostic; see event and exception metadata",
            ["exceptionType"] = logEntry.Exception?.GetType().FullName
        };
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

        foreach (var field in fields.Take(64))
        {
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
    }

    private static bool IsCodeIdentifier(string value) => value.Length is >= 1 and <= 96
        && char.IsAsciiLetter(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private sealed class ScopeBudgetReachedException : Exception;
}
