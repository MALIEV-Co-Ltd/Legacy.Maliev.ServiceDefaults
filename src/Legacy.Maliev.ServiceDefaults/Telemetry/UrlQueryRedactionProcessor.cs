using System.Diagnostics;
using OpenTelemetry;

namespace Maliev.Aspire.ServiceDefaults.Telemetry;

/// <summary>
/// Removes query strings and dynamic path identifiers from telemetry URL attributes before export.
/// </summary>
public sealed class UrlQueryRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlAttributeNames =
    [
        "url.full",
        "url.path",
        "http.url",
        "http.target"
    ];

    private static readonly string[] SensitiveSuffixAttributeNames = ["url.query", "url.fragment"];

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        foreach (var attributeName in UrlAttributeNames)
        {
            if (data.GetTagItem(attributeName) is string value)
            {
                data.SetTag(attributeName, RedactUrl(value));
            }
        }

        foreach (var attributeName in SensitiveSuffixAttributeNames)
        {
            if (data.GetTagItem(attributeName) is string)
            {
                data.SetTag(attributeName, "<redacted>");
            }
        }
    }

    private static string RedactUrl(string value)
    {
        var queryIndex = value.IndexOf('?');
        var fragmentIndex = value.IndexOf('#');
        var suffixIndex = queryIndex < 0 ? fragmentIndex :
            fragmentIndex < 0 ? queryIndex : Math.Min(queryIndex, fragmentIndex);
        var pathEnd = suffixIndex < 0 ? value.Length : suffixIndex;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeIndex < 0 ? 0 : value.IndexOf('/', schemeIndex + 3);
        if (pathStart < 0 || pathStart >= pathEnd)
        {
            return AppendRedactedSuffix(value[..pathEnd], queryIndex, fragmentIndex);
        }

        var path = value[pathStart..pathEnd];
        var segments = path.Split('/', StringSplitOptions.None);
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0)
            {
                continue;
            }

            var decoded = Uri.UnescapeDataString(segments[index]);
            if (decoded.All(char.IsDigit) || Guid.TryParse(decoded, out _))
            {
                segments[index] = "{id}";
            }
            else if (decoded.Contains('@', StringComparison.Ordinal) ||
                     decoded.Contains('.', StringComparison.Ordinal) ||
                     decoded.Contains('/', StringComparison.Ordinal) ||
                     decoded.Length > 64)
            {
                segments[index] = "{identifier}";
            }
        }

        return AppendRedactedSuffix(value[..pathStart] + string.Join('/', segments), queryIndex, fragmentIndex);
    }

    private static string AppendRedactedSuffix(string value, int queryIndex, int fragmentIndex)
    {
        if (queryIndex >= 0 && (fragmentIndex < 0 || queryIndex < fragmentIndex))
        {
            return value + "?<redacted>";
        }

        return fragmentIndex >= 0 ? value + "#<redacted>" : value;
    }
}
