using System.Net;
using System.Text.Json;

namespace Legacy.Maliev.ServiceDefaults.Tests;

[Collection("Native console output")]
public sealed class PrivateRequestObservationLateRoutingTests
{
    [Theory]
    [InlineData("/review/readiness")]
    [InlineData("/review/liveness")]
    public async Task RegisteredHealthyGet_LateSelectedEndpointStillReceivesProtectedMarker(string path)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(lateRouting: true);
        using var response = await host.SendAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertLateSelection(host);
        AssertMarker(response);
        if (path == "/review/liveness")
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        else
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("Healthy", json.RootElement.GetProperty("checks")
                .GetProperty("controlled-health").GetProperty("status").GetString());
        }
        Assert.Empty(host.Records.Failures);
    }

    [Fact]
    public async Task RegisteredUnhealthyGet_LateSelectionKeepsSanitizedFailureBodyAndMarker()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(lateRouting: true);
        host.Status = 503;
        using var response = await host.SendAsync("/review/readiness");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertLateSelection(host);
        AssertMarker(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        var check = json.RootElement.GetProperty("checks").GetProperty("controlled-health");
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.False(check.TryGetProperty("exception", out _));
        Assert.False(check.TryGetProperty("description", out _));
        var observation = Assert.Single(host.Records.Observations);
        Assert.Equal("HealthProbeFailure", observation.State["EventName"]);
        Assert.Equal("Readiness", observation.State["Operation"]);
        Assert.Equal(503, observation.State["StatusCode"]);
        Assert.Null(observation.ExceptionType);
    }

    [Theory]
    [InlineData("/review/readiness")]
    [InlineData("/review/liveness")]
    public async Task RegisteredGet_LateSelectionRestoresMarkerAfterActualDownstreamStartOverwrite(string path)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(
            rewriteHeaders: true, lateRouting: true);
        using var response = await host.SendAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertLateSelection(host);
        Assert.Equal(1, host.HealthOverwriteCallbacks);
        AssertMarker(response);
        Assert.Empty(host.Records.Failures);
    }

    [Theory]
    [InlineData("business", "/review/readiness", "GET", 200)]
    [InlineData("registered", "/review/readiness", "POST", 200)]
    [InlineData("registered", "/review/aspire-liveness", "GET", 200)]
    [InlineData("unmapped", "/review/readiness", "GET", 404)]
    public async Task LateRouting_DoesNotTurnPathLookalikesOrOtherMethodsIntoHealthMarkers(
        string arrangement, string path, string method, int status)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(
            mapHealth: arrangement == "registered", mapBusinessReadiness: arrangement == "business",
            lateRouting: true);
        using var response = await host.SendAsync(path, method);
        Assert.Equal(status, (int)response.StatusCode);
        AssertLateSelection(host);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        if (arrangement == "business")
            Assert.Equal("Business response", await response.Content.ReadAsStringAsync());
        else if (path == "/review/aspire-liveness")
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Records.Failures);
    }

    [Fact]
    public async Task MappedHealthPath_ShortCircuitedBeforeRoutingDoesNotReceiveMarker()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(
            lateRouting: true, shortCircuitBeforeLateRouting: true);
        using var response = await host.SendAsync("/review/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Benign pre-routing response", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, host.LateRequestsWithoutEndpoint);
        Assert.Equal(0, host.LateRequestsWithEndpoint);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        Assert.Empty(host.Records.Failures);
    }

    [Fact]
    public async Task AlreadyStartedNonHealthResponse_RealObserverContinuesToMappedEndpointWithoutHeadersOrFailures()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(
            lateRouting: true, startedResponseBeforeObserver: true);
        using var response = await host.SendAsync("/fixture/business");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Benign started response", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, host.StartedResponsesBeforeObserver);
        AssertLateSelection(host);
        Assert.Equal(1, host.DownstreamCalls);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        Assert.Empty(host.Records.Failures);
    }

    private static void AssertLateSelection(PrivateRequestObservationHttpFixture host)
    {
        Assert.Equal(1, host.LateRequestsWithoutEndpoint);
        Assert.Equal(1, host.LateRequestsWithEndpoint);
    }

    private static void AssertMarker(HttpResponseMessage response)
    {
        Assert.True(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Matches("^[a-f0-9]{32}$", Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
    }
}
