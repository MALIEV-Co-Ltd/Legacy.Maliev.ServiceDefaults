using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

internal sealed record PrivateRequestObservationSelection(string ServicePrefix);
internal sealed record PrivateRequestHealthEndpoint(string Operation);

internal sealed class PrivateRequestObservationState(
    PrivateRequestObservationSelection selection, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReadiness = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLiveness = DateTimeOffset.MinValue;
    private int _readinessStatus;
    private int _livenessStatus;

    public bool AdmitProbe()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (now - _lastProbe < TimeSpan.FromMinutes(1)) return false;
            _lastProbe = now;
            return true;
        }
    }

    public bool IsRegisteredGetHealth(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) return false;
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<PrivateRequestHealthEndpoint>();
        return endpoint is not null && context.Request.Path.Equals(
            new PathString($"/{selection.ServicePrefix}/{endpoint.Operation}"));
    }

    public void AttachHeaders(HttpContext context, string? nonce = null)
    {
        WriteHeaders(context, nonce);
        context.Response.OnStarting(() =>
        {
            WriteHeaders(context, nonce);
            return Task.CompletedTask;
        });
    }

    public void WriteHeaders(HttpContext context, string? nonce)
    {
        context.Response.Headers["X-Maliev-Health-Instance"] = _instance;
        context.Response.Headers.CacheControl = "no-store";
        if (nonce is not null)
        {
            context.Response.Headers["X-Maliev-Diagnostic-Id"] = nonce;
            context.Response.Headers["X-Robots-Tag"] = "noindex";
        }
    }

    public void RecordCompletedResponse(HttpContext context, ILogger logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        bool readiness = path.EndsWith("/readiness", StringComparison.OrdinalIgnoreCase);
        bool liveness = path.EndsWith("/liveness", StringComparison.OrdinalIgnoreCase);
        int status = context.Response.StatusCode;
        if (readiness || liveness)
        {
            lock (_gate)
            {
                ref int previousStatus = ref (readiness ? ref _readinessStatus : ref _livenessStatus);
                ref DateTimeOffset previousAt = ref (readiness ? ref _lastReadiness : ref _lastLiveness);
                if (status < 500)
                {
                    previousStatus = 0;
                    previousAt = DateTimeOffset.MinValue;
                    return;
                }
                var now = clock.GetUtcNow();
                if (status == previousStatus && now - previousAt < TimeSpan.FromMinutes(5)) return;
                previousStatus = status;
                previousAt = now;
            }
        }
        if (status >= 500)
        {
            logger.LogError("{EventName} Operation={Operation} StatusCode={StatusCode}",
                readiness || liveness ? "HealthProbeFailure" : "HandledOperationFailure",
                readiness ? "Readiness" : liveness ? "Liveness" : "HttpResponse", status);
        }
    }
}
