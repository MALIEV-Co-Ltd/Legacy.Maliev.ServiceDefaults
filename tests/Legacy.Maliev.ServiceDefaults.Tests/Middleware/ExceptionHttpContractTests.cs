using System.Net;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Legacy.Maliev.ServiceDefaults.Tests.Middleware;

public sealed class ExceptionHttpContractTests
{
    [Theory]
    [InlineData("unauthorized", 401, "Unauthorized access")]
    [InlineData("timeout", 408, "Request timeout")]
    [InlineData("uncancelled", 408, "Request was cancelled")]
    [InlineData("unimplemented", 501, "Feature not implemented")]
    [InlineData("domain-unavailable", 503, "The requested service is temporarily unavailable.")]
    [InlineData("domain-validation", 400, "The request is invalid.")]
    public async Task Production_HTTP_boundary_preserves_status_and_redacts_failure_details(
        string failure, int status, string message)
    {
        Exception exception = failure switch
        {
            "unauthorized" => new UnauthorizedAccessException("private-synthetic-detail"),
            "timeout" => new TimeoutException("private-synthetic-detail"),
            "uncancelled" => new OperationCanceledException("private-synthetic-detail"),
            "unimplemented" => new NotImplementedException("private-synthetic-detail"),
            "domain-unavailable" => new SampleServiceUnavailableException("private-synthetic-detail"),
            "domain-validation" => new SampleValidationException("private-synthetic-detail"),
            _ => throw new ArgumentException("Unknown fixture case.", nameof(failure)),
        };

        using var document = await SendFailureAsync(exception, Environments.Production, (HttpStatusCode)status);
        AssertError(document, status, message);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("private-synthetic-detail", document.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, "A duplicate record already exists")]
    [InlineData(null, true, "A duplicate record already exists")]
    [InlineData("IX_Customers_Email", false, "A record with this email already exists")]
    [InlineData("IX_Customers_Email", true, "A record with this email already exists")]
    [InlineData("IX_Accounts_Username", false, "A record with this username already exists")]
    [InlineData("IX_Accounts_Username", true, "A record with this username already exists")]
    [InlineData("IX_Invoices_InvoiceNumber", false, "A record with this invoice number already exists")]
    [InlineData("IX_Invoices_InvoiceNumber", true, "A record with this invoice number already exists")]
    [InlineData("IX_Records_ExternalReference", false, "A record with this value already exists. Please use a unique value.")]
    [InlineData("IX_Records_ExternalReference", true, "A record with this value already exists. Please use a unique value.")]
    public async Task Actual_Npgsql_unique_violation_preserves_sanitized_409_through_HTTP(
        string? constraint, bool wrapped, string message)
    {
        // Actual Npgsql exception shape; no database connection or production row is involved.
        var postgres = new PostgresException("private-synthetic-detail", "ERROR", "ERROR", "23505",
            detail: "private-synthetic-value", constraintName: constraint);
        Exception failure = wrapped ? new Exception("private-wrapper-detail", postgres) : postgres;

        using var document = await SendFailureAsync(failure, Environments.Production, HttpStatusCode.Conflict);
        AssertError(document, 409, message);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("private-", document.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_HTTP_boundary_retains_diagnostic_details_without_changing_client_error()
    {
        using var document = await SendFailureAsync(new TimeoutException("synthetic-development-detail"),
            Environments.Development, HttpStatusCode.RequestTimeout);
        AssertError(document, 408, "Request timeout");
        Assert.Contains("synthetic-development-detail", document.RootElement.GetProperty("details").GetString(),
            StringComparison.Ordinal);
    }

    private static async Task<JsonDocument> SendFailureAsync(Exception failure, string environment, HttpStatusCode status)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.Run(_ => Task.FromException(failure));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/fixture/failure");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertError(JsonDocument document, int status, string message)
    {
        var root = document.RootElement;
        Assert.Equal(status, root.GetProperty("statusCode").GetInt32());
        Assert.Equal(message, root.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    private sealed class SampleServiceUnavailableException(string message) : Exception(message);
    private sealed class SampleValidationException(string message) : Exception(message);
}
