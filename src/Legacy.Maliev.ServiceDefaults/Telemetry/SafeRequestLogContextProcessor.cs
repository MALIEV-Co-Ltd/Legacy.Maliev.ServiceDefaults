using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Maliev.Aspire.ServiceDefaults.Telemetry;

/// <summary>
/// Adds only reviewed request context to OTLP logs while ILogger scopes remain disabled.
/// </summary>
internal sealed class SafeRequestLogContextProcessor(IHttpContextAccessor accessor) : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        // OnEnd runs inline with ILogger.Log, while the request AsyncLocal is available.
        // Never retain HttpContext or values beyond this callback.
        HttpContext? context = accessor.HttpContext;
        if (context?.Items[CorrelationIdMiddleware.ValidatedCorrelationIdKey] is not string correlationId)
        {
            return;
        }

        var attributes = record.Attributes?.Where(item => item.Key is not
            ("CorrelationId" or "RouteTemplate")).ToList() ?? [];
        attributes.Add(new("CorrelationId", correlationId));
        attributes.Add(new("RouteTemplate", RouteLogPath.FromContext(context)));
        record.Attributes = attributes;
    }
}
