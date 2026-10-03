using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

internal static class PrivateRequestObservationPipeline
{
    private const string DiagnosticPath = "/internal/diagnostics/observability";
    private static readonly object NonceKey = new();

    public static void UseDiagnosticBoundary(IApplicationBuilder app, PrivateRequestObservationState state)
    {
        var factory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateRequestObservation");
        // The public SDK middleware isolates this controlled branch from consumer IExceptionHandler services.
        var framework = new ExceptionHandlerMiddleware(context =>
        {
            string nonce = (string)context.Items[NonceKey]!;
            logger.LogWarning("{EventName} Synthetic={Synthetic} DiagnosticId={DiagnosticId}",
                "ObservabilityPipelineProbe", true, nonce);
            throw new ProductionObservabilityDiagnosticException();
        }, factory, Options.Create(new ExceptionHandlerOptions
        {
            ExceptionHandler = context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";
                state.WriteHeaders(context, (string)context.Items[NonceKey]!);
                return Task.CompletedTask;
            },
            SuppressDiagnosticsCallback = _ => false
        }), app.ApplicationServices.GetRequiredService<DiagnosticListener>());

        app.Use(async (context, next) =>
        {
            if (context.Request.Path != DiagnosticPath)
            {
                await next(context);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Robots-Tag"] = "noindex";
            var values = context.Request.Headers["X-Maliev-Diagnostic-Id"];
            if (!HttpMethods.IsGet(context.Request.Method)
                || context.Connection.RemoteIpAddress is null
                || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
                || values.Count != 1 || !Guid.TryParseExact(values[0], "N", out var id))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!state.AdmitProbe())
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            string nonce = id.ToString("N");
            context.Items[NonceKey] = nonce;
            state.AttachHeaders(context, nonce);
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["Synthetic"] = true,
                ["DiagnosticId"] = nonce
            }))
            {
                await framework.Invoke(context);
                // .NET 10 emits the real SDK Error after its handler delegate returns.
                logger.LogCritical("{EventName} Synthetic={Synthetic} DiagnosticId={DiagnosticId} StatusCode={StatusCode}",
                    "UnhandledRequestFailure", true, nonce, StatusCodes.Status500InternalServerError);
            }
        });
    }

    public static void UseCompletedResponseObserver(IApplicationBuilder app, PrivateRequestObservationState state)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateRequestObservation");
        app.Use(async (context, next) =>
        {
            if (state.IsRegisteredGetHealth(context)) state.AttachHeaders(context);
            else if (!context.Response.HasStarted)
            {
                // Routing may select the registered endpoint downstream of this observer.
                context.Response.OnStarting(() =>
                {
                    if (state.IsRegisteredGetHealth(context)) state.WriteHeaders(context, null);
                    return Task.CompletedTask;
                });
            }
            await next(context);
            // Thrown failures escape to the existing outer MALIEV handler, never a second observer incident.
            state.RecordCompletedResponse(context, logger);
        });
    }
}
