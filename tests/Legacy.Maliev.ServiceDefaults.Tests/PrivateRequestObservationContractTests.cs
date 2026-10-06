using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.ServiceDefaults.Tests;

[Collection("Native console output")]
public sealed class PrivateRequestObservationContractTests
{
    private const string Diagnostic = "/internal/diagnostics/observability";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/review")]
    [InlineData("review/path")]
    [InlineData("review?query")]
    [InlineData("review#fragment")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Registration_InvalidOrUnboundedPrefixIsRejected(string? prefix)
    {
        var builder = WebApplication.CreateBuilder();
        using var configuration = builder.Configuration;
        Assert.ThrowsAny<ArgumentException>(() => builder.AddPrivateRequestObservation(prefix!));
    }

    [Fact]
    public void Registration_NullBuilderIsRejected()
    {
        IHostApplicationBuilder builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.AddPrivateRequestObservation("review"));
    }

    [Fact]
    public void Registration_ConflictingSecondPrefixIsRejected()
    {
        var builder = WebApplication.CreateBuilder();
        using var configuration = builder.Configuration;
        builder.AddPrivateRequestObservation("review");
        Assert.Throws<InvalidOperationException>(() => builder.AddPrivateRequestObservation("other"));
    }

    [Theory]
    [InlineData("203.0.113.7", "GET", "valid", null)]
    [InlineData(null, "GET", "valid", null)]
    [InlineData("127.0.0.1", "POST", "valid", null)]
    [InlineData("127.0.0.1", "HEAD", "valid", null)]
    [InlineData("127.0.0.1", "GET", "missing", null)]
    [InlineData("127.0.0.1", "GET", "malformed", null)]
    [InlineData("127.0.0.1", "GET", "braced", null)]
    [InlineData("127.0.0.1", "GET", "multiple", null)]
    [InlineData("203.0.113.7", "GET", "valid", "127.0.0.1")]
    public async Task Diagnostic_DenialIsQuietDirectAndNeverCallsBusinessBoundary(
        string? peer, string method, string kind, string? forwarded)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(peer);
        string[]? nonce = kind switch
        {
            "missing" => null,
            "malformed" => ["not-a-guid"],
            "braced" => ["{01234567-89ab-cdef-0123-456789abcdef}"],
            "multiple" => [Nonce, Nonce],
            _ => [Nonce]
        };
        using var response = await host.SendAsync(Diagnostic, method, nonce, forwarded);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Equal(0, host.DownstreamCalls);
        Assert.Empty(host.Records.Failures);
        Assert.DoesNotContain(host.Records.Snapshot, record => record.Category.EndsWith("RequestLoggingMiddleware", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("127.0.0.1", "0123456789ABCDEF0123456789ABCDEF", Nonce)]
    [InlineData("::1", Nonce, Nonce)]
    [InlineData("127.0.0.1", "00000000000000000000000000000000", "00000000000000000000000000000000")]
    public async Task Diagnostic_UsesRealFrameworkFailurePipelineAndCanonicalNonce(
        string peer, string supplied, string expected)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(peer);
        using var response = await host.SendAsync(Diagnostic, nonce: [supplied]);
        await AssertSyntheticResponse(response, expected);
        Assert.Equal(0, host.DownstreamCalls);
        var events = host.Records.Failures;
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical }, events.Select(record => record.Level).ToArray());
        Assert.Equal("ObservabilityPipelineProbe", events[0].State["EventName"]);
        Assert.Equal("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", events[1].Category);
        Assert.Equal(1, events[1].EventId.Id);
        Assert.Equal("UnhandledException", events[1].EventId.Name);
        Assert.Equal("Maliev.Aspire.ServiceDefaults.Diagnostics.ProductionObservabilityDiagnosticException", events[1].ExceptionType);
        Assert.Equal("UnhandledRequestFailure", events[2].State["EventName"]);
        Assert.Null(events[0].ExceptionType);
        Assert.Null(events[2].ExceptionType);
        Assert.All(events, record =>
        {
            Assert.False(string.IsNullOrEmpty(record.PrivateJson));
            using var json = JsonDocument.Parse(record.PrivateJson);
            Assert.True(json.RootElement.TryGetProperty("Synthetic", out _));
            Assert.True(json.RootElement.TryGetProperty("DiagnosticId", out _));
            Assert.True(json.RootElement.GetProperty("Synthetic").GetBoolean());
            Assert.Equal(expected, json.RootElement.GetProperty("DiagnosticId").GetString());
            Assert.DoesNotContain("not-a-guid", record.PrivateJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Diagnostic_RepeatIsQuietUntilExactOneMinuteBoundary()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var first = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(first, Nonce);
        host.Records.Clear();
        host.Clock.Now += TimeSpan.FromSeconds(59);
        using var repeat = await host.SendAsync(Diagnostic, nonce: ["11111111111111111111111111111111"]);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeat.StatusCode);
        Assert.Null(repeat.Headers.Location);
        Assert.Equal(string.Empty, await repeat.Content.ReadAsStringAsync());
        Assert.Equal("no-store", repeat.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", Assert.Single(repeat.Headers.GetValues("X-Robots-Tag")));
        Assert.False(repeat.Headers.Contains("X-Maliev-Diagnostic-Id"));
        Assert.False(repeat.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Empty(host.Records.Failures);
        host.Clock.Now += TimeSpan.FromSeconds(1);
        using var admitted = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(admitted, Nonce);
        Assert.Equal(3, host.Records.Failures.Length);
    }

    [Fact]
    public async Task Diagnostic_InvalidNonceDoesNotConsumeAdmission()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var denied = await host.SendAsync(Diagnostic, nonce: ["invalid"]);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty(host.Records.Failures);
        using var admitted = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(admitted, Nonce);
    }

    [Fact]
    public async Task Diagnostic_ConcurrentRequestsHaveOneAdmission()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SendAsync(Diagnostic, nonce: [Nonce])));
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.InternalServerError));
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
            Assert.Equal(3, host.Records.Failures.Length);
            Assert.Equal(0, host.DownstreamCalls);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task DefaultOff_PreservesExistingOrdinaryPipelineAndNoMarker()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(selected: false);
        using var diagnostic = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        Assert.Equal(HttpStatusCode.NotFound, diagnostic.StatusCode);
        Assert.Equal(1, host.DownstreamCalls);
        Assert.False(diagnostic.Headers.Contains("X-Maliev-Health-Instance"));
        using var health = await host.SendAsync("/review/liveness");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        Assert.False(health.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Empty(host.Records.Failures);
    }

    [Fact]
    public async Task OrdinaryThrownFailure_PreservesJsonAndOneExistingIncidentOwner()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var response = await host.SendAsync("/ordinary-failure");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-ordinary-exception-sentinel", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("An internal server error occurred", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
        var failure = Assert.Single(host.Records.Failures);
        Assert.Equal(LogLevel.Critical, failure.Level);
        Assert.Equal("Maliev.Aspire.ServiceDefaults.Middleware.ExceptionHandlingMiddleware", failure.Category);
        Assert.Null(failure.ExceptionType);
        Assert.Empty(host.Records.Observations);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
    }

    [Theory]
    [InlineData("/ordinary-failure")]
    [InlineData("/throw/readiness")]
    public async Task CompletedResponse_DownstreamExceptionReexecutionKeepsExistingFailureOwner(string path)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(downstreamExceptionHandler: true);
        using var response = await host.SendAsync(path);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Benign exception response", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, host.ExceptionHandlerCalls);
        Assert.Equal(path, host.HandledExceptionPath);
        Assert.Single(host.Records.Snapshot, record => record.Level == LogLevel.Critical
            && record.State.GetValueOrDefault("EventName") as string == "FixtureOwnedUnhandledFailure");
        Assert.Empty(host.Records.Observations);
    }

    [Fact]
    public async Task CompletedResponse_IncidentHeaderAloneDoesNotSuppressReturnedFailure()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(incidentHeaderOnReturnedFailure: true);
        host.Status = 500;
        using var response = await host.SendAsync("/fixture/ordinary");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("fixture-header-only", Assert.Single(response.Headers.GetValues("X-Incident-Id")));
        Assert.Equal("HandledOperationFailure", Assert.Single(host.Records.Observations).State["EventName"]);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(400, 0)]
    [InlineData(404, 0)]
    [InlineData(500, 1)]
    [InlineData(503, 1)]
    public async Task CompletedResponse_ObservesOnlyReturnedServerFailures(int status, int count)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.Status = status;
        using var response = await host.SendAsync("/fixture/ordinary");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(count, host.Records.Observations.Length);
        Assert.All(host.Records.Observations, record =>
        {
            Assert.Equal(LogLevel.Error, record.Level);
            Assert.Null(record.ExceptionType);
            Assert.Equal("HandledOperationFailure", record.State["EventName"]);
            Assert.Equal("HttpResponse", record.State["Operation"]);
            Assert.Equal(status, record.State["StatusCode"]);
        });
    }

    [Theory]
    [InlineData("/review/liveness")]
    [InlineData("/review/readiness")]
    public async Task RegisteredGetHealth_IsQuietUncachedAndKeepsItsBody(string path)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var response = await host.SendAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMarker(response);
        if (path == "/review/liveness")
        {
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("Healthy", json.RootElement.GetProperty("checks").GetProperty("controlled-health").GetProperty("status").GetString());
        }
        Assert.Empty(host.Records.Failures);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public async Task RegisteredUnhealthyReadiness_KeepsSanitizedBodyAndSeparatesMarkerFromFailure(
        string method, bool marker)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.Status = 503;
        using var response = await host.SendAsync("/review/readiness", method);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(marker, response.Headers.Contains("X-Maliev-Health-Instance"));
        if (marker) AssertMarker(response);
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
    [InlineData("/review/readiness", "POST")]
    [InlineData("/review/liveness", "POST")]
    [InlineData("/review/aspire-liveness", "GET")]
    [InlineData("/review/metrics", "GET")]
    [InlineData("/fixture/readiness", "GET")]
    [InlineData("/reviews/readiness", "GET")]
    public async Task OtherPathsAndMethods_HaveNoNewHealthMarker(string path, string method)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var response = await host.SendAsync(path, method);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Empty(host.Records.Observations);
    }

    [Fact]
    public async Task ConfiguredButUnregisteredHealth_HasNoMarker()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(mapHealth: false);
        using var response = await host.SendAsync("/review/liveness");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
    }

    [Fact]
    public async Task BusinessGetAtConfiguredReadinessPath_IsNotRegisteredHealth()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(
            mapHealth: false, mapBusinessReadiness: true);
        using var response = await host.SendAsync("/review/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Business response", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        Assert.Empty(host.Records.Observations);
    }

    [Fact]
    public async Task SyntheticHeaders_AreProtectedAgainstLaterResponseStartOverwrite()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(rewriteSyntheticHeaders: true);
        using var response = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(response, Nonce);
        Assert.Equal(1, host.SyntheticOverwriteCallbacks);
        Assert.Equal(3, host.Records.Failures.Length);
    }

    [Fact]
    public async Task HealthIdentityAndThrottle_AreStableInsideOneHostAndIndependentBetweenHosts()
    {
        await using var first = await PrivateRequestObservationHttpFixture.CreateAsync();
        await using var second = await PrivateRequestObservationHttpFixture.CreateAsync();
        using var health = await first.SendAsync("/review/liveness");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        string marker = AssertMarker(health);
        using var readiness = await first.SendAsync("/review/readiness");
        Assert.Equal(marker, AssertMarker(readiness));
        using var diagnostic = await first.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(diagnostic, Nonce);
        Assert.Equal(marker, AssertMarker(diagnostic));
        using var independent = await second.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(independent, Nonce);
        Assert.NotEqual(marker, AssertMarker(independent));
    }

    [Fact]
    public async Task HealthHeaders_AreRestoredAtResponseStart()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(rewriteHeaders: true);
        using var response = await host.SendAsync("/review/liveness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMarker(response);
    }

    [Theory]
    [InlineData("/fixture/readiness", "POST", "Readiness")]
    [InlineData("/fixture/liveness", "GET", "Liveness")]
    [InlineData("/fixture/ReAdInEsS", "POST", "Readiness")]
    [InlineData("/fixture/LiVeNeSs", "GET", "Liveness")]
    public async Task SuffixHealthFailures_KeepSourceBoundsDespiteUnregisteredHealthOrNonGet(
        string path, string method, string operation)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.Status = 503;
        for (int index = 0; index < 20; index++)
        {
            using var response = await host.SendAsync(path, method);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
        }
        Assert.Single(host.Records.Observations);
        host.Status = 500;
        using (var changed = await host.SendAsync(path, method)) Assert.Equal(HttpStatusCode.InternalServerError, changed.StatusCode);
        Assert.Equal(2, host.Records.Observations.Length);
        host.Clock.Now += TimeSpan.FromSeconds(299);
        using (var bounded = await host.SendAsync(path, method)) Assert.Equal(HttpStatusCode.InternalServerError, bounded.StatusCode);
        Assert.Equal(2, host.Records.Observations.Length);
        host.Clock.Now += TimeSpan.FromSeconds(1);
        using (var expired = await host.SendAsync(path, method)) Assert.Equal(HttpStatusCode.InternalServerError, expired.StatusCode);
        Assert.Equal(3, host.Records.Observations.Length);
        Assert.All(host.Records.Observations, record =>
        {
            Assert.Equal("HealthProbeFailure", record.State["EventName"]);
            Assert.Equal(operation, record.State["Operation"]);
            Assert.Null(record.ExceptionType);
        });
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    public async Task SuffixHealth_AnyBelow500ResponseResetsFailureFingerprint(int recovery)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.Status = 503;
        using (var failed = await host.SendAsync("/fixture/readiness", "POST")) Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Single(host.Records.Observations);
        host.Status = recovery;
        using (var recovered = await host.SendAsync("/fixture/readiness", "POST")) Assert.Equal(recovery, (int)recovered.StatusCode);
        Assert.Single(host.Records.Observations);
        host.Status = 503;
        using (var failedAgain = await host.SendAsync("/fixture/readiness", "POST")) Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAgain.StatusCode);
        Assert.Equal(2, host.Records.Observations.Length);
    }

    [Fact]
    public async Task SuffixHealth_ReadinessAndLivenessHaveIndependentFingerprints()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.Status = 503;
        using (var readiness = await host.SendAsync("/fixture/readiness")) Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        using (var liveness = await host.SendAsync("/fixture/liveness")) Assert.Equal(HttpStatusCode.ServiceUnavailable, liveness.StatusCode);
        Assert.Equal(new[] { "Readiness", "Liveness" }, host.Records.Observations.Select(record => record.State["Operation"] as string).ToArray());
    }

    [Fact]
    public async Task RepeatedRegistration_DoesNotDuplicateAdmissionOrFailureEvents()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync(repeatRegistration: true);
        using var response = await host.SendAsync(Diagnostic, nonce: [Nonce]);
        await AssertSyntheticResponse(response, Nonce);
        Assert.Equal(3, host.Records.Failures.Length);
    }

    [Fact]
    public async Task CapturedEntry_CharacterizesExistingNativeFormatter()
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.EmitNativeControl();
        var record = Assert.Single(host.Records.Snapshot, record => record.Category == "Fixture.Native");
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal(string.Empty, record.PrivateJson);
        using var json = JsonDocument.Parse(record.NativeJson);
        Assert.Equal("INFO", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Benign native event", json.RootElement.GetProperty("Message").GetString());
        Assert.True(json.RootElement.TryGetProperty("Timestamp", out _));
    }

    [Fact]
    public async Task ExplicitSelection_RetainsActualNativeConsoleProviderOutput()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await using (var host = await PrivateRequestObservationHttpFixture.CreateAsync())
            {
                host.EmitNativeControl();
            } // Drain the actual console provider before restoring the process output.
        }
        finally { Console.SetOut(original); }
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = lines.Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }).ToArray();
        var native = Assert.Single(entries, entry => entry.GetProperty("Category").GetString() == "Fixture.Native");
        Assert.Equal("INFO", native.GetProperty("severity").GetString());
        Assert.Equal("Benign native event", native.GetProperty("Message").GetString());
        Assert.True(native.TryGetProperty("Timestamp", out _));
    }

    [Theory]
    [InlineData(true, Nonce, Nonce)]
    [InlineData(true, "0123456789ABCDEF0123456789ABCDEF", Nonce)]
    [InlineData(true, "00000000000000000000000000000000", "00000000000000000000000000000000")]
    [InlineData(false, Nonce, null)]
    [InlineData("true", Nonce, null)]
    [InlineData(true, null, null)]
    [InlineData(true, "private-nonce-sentinel", null)]
    [InlineData(true, "01234567-89ab-cdef-0123-456789abcdef", null)]
    public async Task PrivateFormatter_AllowsOnlyTrueSyntheticWithStrictCanonicalGuidN(
        object synthetic, string? supplied, string? expected)
    {
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.EmitSyntheticFields(synthetic, supplied);
        var record = Assert.Single(host.Records.Snapshot, entry => entry.Category == "Fixture.Allowlist");
        using var json = JsonDocument.Parse(record.PrivateJson);
        Assert.False(json.RootElement.TryGetProperty("Credential", out _));
        Assert.DoesNotContain("private-", record.PrivateJson, StringComparison.Ordinal);
        if (expected is null)
        {
            Assert.False(json.RootElement.TryGetProperty("Synthetic", out _));
            Assert.False(json.RootElement.TryGetProperty("DiagnosticId", out _));
        }
        else
        {
            Assert.True(json.RootElement.TryGetProperty("Synthetic", out _));
            Assert.True(json.RootElement.TryGetProperty("DiagnosticId", out _));
            Assert.True(json.RootElement.GetProperty("Synthetic").GetBoolean());
            Assert.Equal(expected, json.RootElement.GetProperty("DiagnosticId").GetString());
        }
    }

    [Fact]
    public async Task Diagnostic_ActualNativeProviderEmitsOrderedSyntheticSeverityAndValidatedScope()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await using (var host = await PrivateRequestObservationHttpFixture.CreateAsync())
            {
                using var response = await host.SendAsync(Diagnostic, nonce: [Nonce]);
                await AssertSyntheticResponse(response, Nonce);
            }
        }
        finally { Console.SetOut(original); }
        var entries = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Clone();
            }).Where(entry => entry.GetProperty("severity").GetString() is "WARNING" or "ERROR" or "CRITICAL").ToArray();
        Assert.Equal(new[] { "WARNING", "ERROR", "CRITICAL" },
            entries.Select(entry => entry.GetProperty("severity").GetString()).ToArray());
        Assert.Equal("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", entries[1].GetProperty("Category").GetString());
        Assert.Equal(1, entries[1].GetProperty("EventId").GetInt32());
        Assert.Equal("ProductionObservabilityDiagnosticException", entries[1].GetProperty("Exception").GetString());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("Exception").ValueKind);
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("Exception").ValueKind);
        Assert.All(entries, entry =>
        {
            Assert.Contains(entry.GetProperty("Scopes").EnumerateArray(), scope =>
                scope.TryGetProperty("Synthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.True
                && scope.TryGetProperty("DiagnosticId", out var diagnostic) && diagnostic.GetString() == Nonce);
        });
    }

    [Theory]
    [InlineData("state-wins", Nonce)]
    [InlineData("invalid-state", "11111111111111111111111111111111")]
    [InlineData("split-state", null)]
    [InlineData("reverse-split", null)]
    [InlineData("split-scopes", null)]
    [InlineData("outer-wins", Nonce)]
    public async Task PrivateFormatter_ValidatesOneContainerPairAndPreservesFirstValidPrecedence(string arrangement, string? expected)
    {
        const string other = "11111111111111111111111111111111";
        Dictionary<string, object?> Pair(string id) => new() { ["Synthetic"] = true, ["DiagnosticId"] = id };
        var state = arrangement switch
        {
            "state-wins" => Pair(Nonce),
            "invalid-state" => new Dictionary<string, object?> { ["Synthetic"] = false, ["DiagnosticId"] = Nonce },
            "split-state" => new Dictionary<string, object?> { ["Synthetic"] = true },
            "reverse-split" => new Dictionary<string, object?> { ["DiagnosticId"] = Nonce },
            _ => new Dictionary<string, object?>()
        };
        Dictionary<string, object?>[] scopes = arrangement switch
        {
            "split-state" => [new() { ["DiagnosticId"] = other }],
            "reverse-split" => [new() { ["Synthetic"] = true }],
            "split-scopes" => [new() { ["Synthetic"] = true }, new() { ["DiagnosticId"] = other }],
            "outer-wins" => [Pair(Nonce), Pair(other)],
            _ => [Pair(other)]
        };
        state["severity"] = "forged";
        state["logger"] = "forged";
        state["Credential"] = "private-credential-sentinel";
        await using var host = await PrivateRequestObservationHttpFixture.CreateAsync();
        host.EmitSyntheticContainers(state, scopes);
        var record = Assert.Single(host.Records.Snapshot, entry => entry.Category == "Fixture.Pair");
        using var json = JsonDocument.Parse(record.PrivateJson);
        var root = json.RootElement;
        Assert.Equal("WARNING", root.GetProperty("severity").GetString());
        Assert.Equal("Fixture.Pair", root.GetProperty("logger").GetString());
        Assert.DoesNotContain("private-", record.PrivateJson, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("Credential", out _));
        if (expected is null)
        {
            Assert.False(root.TryGetProperty("Synthetic", out _));
            Assert.False(root.TryGetProperty("DiagnosticId", out _));
        }
        else
        {
            Assert.True(root.GetProperty("Synthetic").GetBoolean());
            Assert.Equal(expected, root.GetProperty("DiagnosticId").GetString());
        }
    }

    private static async Task AssertSyntheticResponse(HttpResponseMessage response, string nonce)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(nonce, Assert.Single(response.Headers.GetValues("X-Maliev-Diagnostic-Id")));
        AssertMarker(response);
    }

    private static string AssertMarker(HttpResponseMessage response)
    {
        Assert.True(response.Headers.Contains("X-Maliev-Health-Instance"));
        string marker = Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance"));
        Assert.Matches("^[a-f0-9]{32}$", marker);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return marker;
    }
}
