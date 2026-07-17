using System.Diagnostics;
using Maliev.Aspire.ServiceDefaults.Telemetry;

namespace Maliev.Aspire.Tests.Infrastructure;

/// <summary>
/// Tests for URL query redaction in exported telemetry.
/// </summary>
public class UrlQueryRedactionProcessorTests
{
    /// <summary>
    /// Verifies sensitive URL query strings are replaced before export.
    /// </summary>
    [Fact]
    public void OnEnd_RedactsQueryStringFromUrlAttributes()
    {
        using var activity = new Activity("test");
        activity.Start();
        activity.SetTag("url.full", "https://storage.googleapis.com/bucket/file.stl?X-Goog-Signature=secret");
        activity.SetTag("http.url", "https://example.test/path?token=secret");
        activity.SetTag("http.target", "/path?session=secret");

        var processor = new UrlQueryRedactionProcessor();

        processor.OnEnd(activity);

        Assert.Equal("https://storage.googleapis.com/bucket/file.stl?<redacted>", activity.GetTagItem("url.full"));
        Assert.Equal("https://example.test/path?<redacted>", activity.GetTagItem("http.url"));
        Assert.Equal("/path?<redacted>", activity.GetTagItem("http.target"));
    }

    /// <summary>
    /// Verifies URL attributes without query strings are preserved.
    /// </summary>
    [Fact]
    public void OnEnd_LeavesUrlsWithoutQueryUnchanged()
    {
        using var activity = new Activity("test");
        activity.Start();
        activity.SetTag("url.full", "https://example.test/path");

        var processor = new UrlQueryRedactionProcessor();

        processor.OnEnd(activity);

        Assert.Equal("https://example.test/path", activity.GetTagItem("url.full"));
    }
}
