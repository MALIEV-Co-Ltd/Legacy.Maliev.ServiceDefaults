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

        Assert.Equal("https://storage.googleapis.com/bucket/{identifier}?<redacted>", activity.GetTagItem("url.full"));
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

    /// <summary>
    /// Verifies dynamic path values are not exported even when the URL has no query string.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/api/orders/123", "https://example.test/api/orders/{id}")]
    [InlineData("/api/orders/550e8400-e29b-41d4-a716-446655440000", "/api/orders/{id}")]
    [InlineData("https://example.test/api/customers/user%40example.test", "https://example.test/api/customers/{identifier}")]
    [InlineData("/api/customers/user%40example.test#internal-note", "/api/customers/{identifier}#<redacted>")]
    [InlineData("/api/files/opaque%2Fname", "/api/files/{identifier}")]
    [InlineData("/api/files/customer.pdf", "/api/files/{identifier}")]
    [InlineData("/api/files/" + "a" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "/api/files/{identifier}")]
    public void OnEnd_RedactsDynamicPathSegments(string value, string expected)
    {
        using var activity = new Activity("test");
        activity.Start();
        activity.SetTag("url.full", value);
        activity.SetTag("http.url", value);
        activity.SetTag("http.target", value);

        new UrlQueryRedactionProcessor().OnEnd(activity);

        Assert.Equal(expected, activity.GetTagItem("url.full"));
        Assert.Equal(expected, activity.GetTagItem("http.url"));
        Assert.Equal(expected, activity.GetTagItem("http.target"));
    }

    /// <summary>
    /// Verifies split URL attributes cannot bypass full-URL redaction.
    /// </summary>
    [Fact]
    public void OnEnd_RedactsSplitUrlAttributes()
    {
        using var activity = new Activity("test");
        activity.Start();
        activity.SetTag("url.path", "/customers/user%40example.test");
        activity.SetTag("url.query", "access_token=secret");
        activity.SetTag("url.fragment", "customer-note");

        new UrlQueryRedactionProcessor().OnEnd(activity);

        Assert.Equal("/customers/{identifier}", activity.GetTagItem("url.path"));
        Assert.Equal("<redacted>", activity.GetTagItem("url.query"));
        Assert.Equal("<redacted>", activity.GetTagItem("url.fragment"));
    }
}
