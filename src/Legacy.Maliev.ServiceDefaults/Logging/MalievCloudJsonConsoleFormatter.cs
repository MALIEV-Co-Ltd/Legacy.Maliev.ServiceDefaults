using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Maliev.Aspire.ServiceDefaults.Logging;

/// <summary>Preserves the legacy JSON console fields while exposing Cloud Logging severity.</summary>
public sealed class MalievCloudJsonConsoleFormatter(IOptionsMonitor<JsonConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    /// <summary>The registered console formatter name.</summary>
    public const string FormatterName = "maliev-cloud-json";

    /// <inheritdoc />
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        var currentOptions = options.CurrentValue;
        var timestamp = currentOptions.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        var payload = new Dictionary<string, object?>
        {
            ["Timestamp"] = timestamp.ToString(currentOptions.TimestampFormat ?? "O", CultureInfo.InvariantCulture),
            ["EventId"] = logEntry.EventId.Id,
            ["LogLevel"] = logEntry.LogLevel.ToString(),
            ["severity"] = ToCloudSeverity(logEntry.LogLevel),
            ["Category"] = logEntry.Category,
            ["Message"] = logEntry.Formatter(logEntry.State, logEntry.Exception),
            // The built-in formatter serializes Exception.ToString(), including messages and stacks.
            // Emit only the type so adopting Cloud severity cannot propagate protected details.
            ["Exception"] = logEntry.Exception?.GetType().Name,
            ["State"] = GetStructuredState(logEntry.State),
            ["Scopes"] = currentOptions.IncludeScopes ? GetScopes(scopeProvider) : null,
        };
        textWriter.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = currentOptions.JsonWriterOptions.Encoder,
            WriteIndented = currentOptions.JsonWriterOptions.Indented,
        }));
    }

    private static string ToCloudSeverity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };

    private static Dictionary<string, object?>? GetStructuredState<TState>(TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            result[value.Key] = ToSafeValue(value.Value);
        }

        return result.Count == 0 ? null : result;
    }

    private static object? ToSafeValue(object? value) => value switch
    {
        null => null,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString(),
        _ => value.GetType().Name,
    };

    private static List<object?>? GetScopes(IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
        {
            return null;
        }

        var scopes = new List<object?>();
        scopeProvider.ForEachScope((scope, values) =>
        {
            var structured = GetStructuredState(scope);
            if (structured is not null)
            {
                // The ASP.NET Core hosting scope contains a literal RequestPath,
                // including customer identifiers and token-shaped path segments.
                // The service scope supplies a separately sanitized RouteTemplate.
                structured.Remove("RequestPath");
            }

            values.Add(structured ?? ToSafeValue(scope));
        }, scopes);
        return scopes.Count == 0 ? null : scopes;
    }
}
