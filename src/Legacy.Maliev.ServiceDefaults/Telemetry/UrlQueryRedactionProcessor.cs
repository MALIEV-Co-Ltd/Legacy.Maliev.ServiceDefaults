using System.Diagnostics;
using OpenTelemetry;

namespace Maliev.Aspire.ServiceDefaults.Telemetry;

/// <summary>
/// Removes query strings from telemetry URL attributes before export.
/// </summary>
public sealed class UrlQueryRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlAttributeNames =
    [
        "url.full",
        "http.url",
        "http.target"
    ];

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        foreach (var attributeName in UrlAttributeNames)
        {
            if (data.GetTagItem(attributeName) is string value)
            {
                data.SetTag(attributeName, RedactQuery(value));
            }
        }
    }

    private static string RedactQuery(string value)
    {
        var queryIndex = value.IndexOf('?');
        if (queryIndex < 0)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, queryIndex), "?<redacted>");
    }
}
