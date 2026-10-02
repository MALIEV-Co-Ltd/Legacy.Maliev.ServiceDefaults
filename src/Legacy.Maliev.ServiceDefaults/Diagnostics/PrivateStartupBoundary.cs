using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.Aspire.ServiceDefaults.Diagnostics;

/// <summary>Contains startup failures at an explicitly selected private entry-point boundary.</summary>
public static class PrivateStartupBoundary
{
    /// <summary>Runs a synchronous host entry point once and privately reports a failure.</summary>
    /// <param name="startHost">The caller-owned host entry point.</param>
    /// <param name="failureLogger">The explicitly supplied failure metadata logger.</param>
    /// <param name="privateOutput">The isolated private output when no logger is supplied.</param>
    public static void Run(Action startHost, ILogger? failureLogger = null, TextWriter? privateOutput = null)
    {
        try
        {
            startHost();
        }
        catch (Exception exception)
        {
            ReportFailure(exception, failureLogger, privateOutput);
        }
    }

    /// <summary>Runs an asynchronous host entry point once and privately reports a failure.</summary>
    /// <param name="startHost">The caller-owned asynchronous host entry point.</param>
    /// <param name="failureLogger">The explicitly supplied failure metadata logger.</param>
    /// <param name="privateOutput">The isolated private output when no logger is supplied.</param>
    /// <returns>A task completing when startup or private failure reporting finishes.</returns>
    public static async Task RunAsync(Func<Task> startHost, ILogger? failureLogger = null, TextWriter? privateOutput = null)
    {
        try
        {
            await startHost().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFailure(exception, failureLogger, privateOutput);
        }
    }

    /// <summary>Reports fixed startup metadata and sets a failing exit code even if the selected sink fails.</summary>
    /// <param name="exception">The original startup failure.</param>
    /// <param name="failureLogger">The explicitly supplied failure metadata logger.</param>
    /// <param name="privateOutput">The isolated private output when no logger is supplied.</param>
    public static void ReportFailure(Exception exception, ILogger? failureLogger = null, TextWriter? privateOutput = null)
    {
        try
        {
            var eventId = new EventId(5102, "StartupFailure");
            if (failureLogger is not null)
            {
                failureLogger.LogCritical(eventId, "{EventName} during {Operation}", "StartupFailure", "HostInitialization");
            }
            else
            {
                var state = new Dictionary<string, object?>
                {
                    ["EventName"] = "StartupFailure",
                    ["Operation"] = "HostInitialization"
                };
                var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Critical,
                    typeof(PrivateStartupBoundary).FullName!, eventId, state, exception,
                    static (_, _) => "StartupFailure during HostInitialization");
                new PrivateFailureConsoleFormatter().Write(in entry, null, privateOutput ?? Console.Error);
            }
        }
        catch (Exception)
        {
            // Diagnostic sink failures must not cause another emission or escape this boundary.
        }
        finally
        {
            Environment.ExitCode = 1;
        }
    }
}
