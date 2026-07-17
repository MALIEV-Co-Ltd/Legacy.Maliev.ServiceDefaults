using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding MassTransit with RabbitMQ to the application.
/// </summary>
public static class MassTransitExtensions
{
    /// <summary>
    /// Adds MassTransit with RabbitMQ configuration.
    /// Requires "rabbitmq" or "RabbitMQ" connection string to be configured.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Optional action to configure MassTransit consumers and settings.</param>
    /// <param name="configureRabbitMq">Optional action to configure RabbitMQ bus settings (e.g., custom queues, routing).</param>
    /// <returns>The configured builder.</returns>
    /// <exception cref="InvalidOperationException">Thrown when RabbitMQ connection string is not configured.</exception>
    public static IHostApplicationBuilder AddMassTransitWithRabbitMq(
        this IHostApplicationBuilder builder,
        Action<IBusRegistrationConfigurator>? configure = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureRabbitMq = null)
    {
        var rabbitmqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
            ?? builder.Configuration.GetConnectionString("RabbitMQ");

        if (string.IsNullOrEmpty(rabbitmqConnectionString))
        {
            if (builder.Environment.IsEnvironment("Testing"))
            {
                // In Testing environment, the connection string might be configured later by the test infrastructure
                rabbitmqConnectionString = "host=localhost";
            }
            else
            {
                // Log available connection strings for debugging
                var connectionStrings = builder.Configuration.GetSection("ConnectionStrings");
                var availableKeys = connectionStrings.GetChildren().Select(c => c.Key).ToList();

                var errorMessage = "RabbitMQ connection string 'rabbitmq' not configured. " +
                    $"Available connection strings: [{string.Join(", ", availableKeys)}]. " +
                    "RabbitMQ is required in all environments.";

                using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
                var logger = loggerFactory.CreateLogger("MassTransitExtensions");
                logger.LogCritical("FATAL: {ErrorMessage}", errorMessage);

                throw new InvalidOperationException(errorMessage);
            }
        }

        var useInMemory = builder.Environment.IsEnvironment("Testing") &&
            (builder.Configuration["MassTransit:UseInMemory"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
             builder.Configuration["MASSTRANSIT_INMEMORY"]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);

        builder.Services.AddMassTransit(x =>
        {
            x.DisableUsageTelemetry();

            // Allow caller to add consumers and configure
            configure?.Invoke(x);

            if (useInMemory)
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConfigureEndpoints(context);
                });
            }
            else
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    var parsedUri = ParseOrConvertConnectionString(rabbitmqConnectionString);
                    cfg.Host(parsedUri, h =>
                    {
                        h.Heartbeat(TimeSpan.FromSeconds(60));
                    });

                    // Allow custom RabbitMQ configuration (queues, exchanges, routing)
                    if (configureRabbitMq != null)
                    {
                        configureRabbitMq(context, cfg);
                    }
                    else
                    {
                        // Default: auto-configure endpoints
                        cfg.ConfigureEndpoints(context);
                    }
                });
            }
        });

        // Production waits for the bus before accepting requests. System tests start the
        // whole platform in one AppHost, so blocking every service on its RabbitMQ bus can
        // create a local socket storm before tests can even exercise HTTP boundaries.
        var skipBusWait = builder.Environment.IsEnvironment("Testing") &&
            builder.Configuration["MassTransit:SkipBusWait"]?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

        builder.Services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = !skipBusWait; // In tests, don't wait - let bus start in background
            options.StartTimeout = TimeSpan.FromSeconds(60);
            options.StopTimeout = TimeSpan.FromSeconds(30);
        });

        // RabbitMQ health check — skip when using in-memory transport
        if (!useInMemory)
        {
            builder.Services.AddHealthChecks()
                .AddRabbitMQ(async sp =>
                {
                    try
                    {
                        var factory = new RabbitMQ.Client.ConnectionFactory();

                        // Handle both URI and host-based connection strings
                        if (rabbitmqConnectionString.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase) ||
                            rabbitmqConnectionString.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase))
                        {
                            factory.Uri = new Uri(rabbitmqConnectionString);
                        }
                        else
                        {
                            // Assume it's a host or connection string - ConnectionFactory.HostName handles simple hostnames
                            if (rabbitmqConnectionString.Contains("host="))
                            {
                                var parts = rabbitmqConnectionString.Split(';');
                                var hostPart = parts.FirstOrDefault(s => s.Trim().StartsWith("host=", StringComparison.OrdinalIgnoreCase));
                                var hostValue = hostPart?.Split('=')[1];
                                factory.HostName = hostValue ?? "localhost";
                            }
                            else
                            {
                                factory.HostName = rabbitmqConnectionString;
                            }
                        }

                        // Set connection timeout for health check
                        factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(30);
                        factory.ContinuationTimeout = TimeSpan.FromSeconds(30);

                        return await factory.CreateConnectionAsync();
                    }
                    catch (Exception)
                    {
                        // Fallback or rethrow to be caught by health check infra
                        throw;
                    }
                },
                name: "rabbitmq",
                tags: new[] { "ready" },
                timeout: TimeSpan.FromMinutes(2)); // Increased timeout to allow RabbitMQ container to start
        }

        return builder;
    }

    private static Uri ParseOrConvertConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return new Uri("amqp://guest:guest@localhost:5672");

        if (connectionString.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase) ||
            connectionString.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(connectionString);
        }

        if (connectionString.Contains("host=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
            string host = "localhost";
            ushort port = 5672;
            string username = "guest";
            string password = "guest";

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("host=", StringComparison.OrdinalIgnoreCase))
                    host = trimmed.Substring(5).Trim();
                else if (trimmed.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                    ushort.TryParse(trimmed.Substring(5).Trim(), out port);
                else if (trimmed.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
                    username = trimmed.Substring(9).Trim();
                else if (trimmed.StartsWith("user=", StringComparison.OrdinalIgnoreCase))
                    username = trimmed.Substring(5).Trim();
                else if (trimmed.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                    password = trimmed.Substring(9).Trim();
                else if (trimmed.StartsWith("pass=", StringComparison.OrdinalIgnoreCase))
                    password = trimmed.Substring(5).Trim();
            }

            return new Uri($"amqp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{host}:{port}");
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
            return new Uri($"amqp://guest:guest@{connectionString}:5672");

        return uri;
    }
}
