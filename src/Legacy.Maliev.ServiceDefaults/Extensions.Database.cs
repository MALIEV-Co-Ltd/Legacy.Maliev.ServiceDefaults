using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding database context with PostgreSQL to the application.
/// </summary>
public static class DatabaseExtensions
{
    /// <summary>
    /// Adds a PostgreSQL DbContext with resilience and optimized configuration.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to register.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <param name="connectionName">The name of the connection string (defaults to TContext name).</param>
    /// <param name="enableDynamicJson">Whether to enable dynamic JSON support for storing polymorphic types.</param>
    /// <param name="configureOptions">Optional action to configure DbContext options.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddPostgresDbContext<TContext>(
        this IHostApplicationBuilder builder,
        string? connectionName = null,
        bool enableDynamicJson = false,
        Action<IServiceProvider, DbContextOptionsBuilder>? configureOptions = null)
        where TContext : DbContext
    {
        var connStringName = connectionName ?? typeof(TContext).Name;
        var connectionString = builder.Configuration.GetConnectionString(connStringName);

        if (string.IsNullOrEmpty(connectionString))
        {
            // Log available connection strings for debugging (without values for security)
            var connectionStrings = builder.Configuration.GetSection("ConnectionStrings");
            var availableKeys = connectionStrings.GetChildren().Select(c => c.Key).ToList();

            var errorMessage = $"Database connection string '{connStringName}' not configured. " +
                $"Available connection strings: [{string.Join(", ", availableKeys)}]. " +
                $"Environment: {builder.Environment.EnvironmentName}. " +
                $"IMPORTANT: Use Testcontainers for tests, NOT InMemory databases.";

            using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
            var logger = loggerFactory.CreateLogger("DatabaseExtensions");
            logger.LogCritical("FATAL: {ErrorMessage}", errorMessage);

            throw new InvalidOperationException(errorMessage);
        }

        // Enhance connection string with pooling configuration if not already present
        connectionString = EnsureConnectionPooling(connectionString);

        // Build data source (optionally with dynamic JSON)
        Npgsql.NpgsqlDataSource? dataSource = null;
        if (enableDynamicJson)
        {
            var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            dataSource = dataSourceBuilder.Build();
        }

        builder.Services.AddDbContext<TContext>((sp, options) =>
        {
            if (dataSource != null)
            {
                options.UseNpgsql(dataSource, npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorCodesToAdd: null);

                    // Increased from 30s to 120s to handle heavy IAM startup load
                    npgsqlOptions.CommandTimeout(120);
                });
            }
            else
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorCodesToAdd: null);

                    // Increased from 30s to 120s to handle heavy IAM startup load
                    npgsqlOptions.CommandTimeout(120);
                });
            }

            if (builder.Configuration.GetValue("Database:EnableSensitiveDataLogging", false))
            {
                options.EnableSensitiveDataLogging();
            }

            if (builder.Configuration.GetValue(
                "Database:EnableDetailedErrors",
                builder.Environment.IsDevelopment()))
            {
                options.EnableDetailedErrors();
            }

            // Apply custom configuration if provided
            configureOptions?.Invoke(sp, options);
        });

        // Add DbContext to health checks
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TContext>(
                tags: new[] { "db", "ready" });

        return builder;
    }

    /// <summary>
    /// Adds a PostgreSQL DbContext with resilience and optimized configuration.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to register.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configureOptions">Optional action to configure DbContext options.</param>
    /// <param name="connectionName">The name of the connection string (defaults to TContext name).</param>
    /// <param name="enableDynamicJson">Whether to enable dynamic JSON support for storing polymorphic types.</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddPostgresDbContext<TContext>(
        this IHostApplicationBuilder builder,
        Action<DbContextOptionsBuilder>? configureOptions,
        string? connectionName = null,
        bool enableDynamicJson = false)
        where TContext : DbContext
    {
        return builder.AddPostgresDbContext<TContext>(
            connectionName,
            enableDynamicJson,
            (sp, options) => configureOptions?.Invoke(options));
    }

    /// <summary>
    /// Retained only as a fail-closed compatibility shim for legacy callers.
    /// Database migrations are owned by the isolated AppHost MigrationRunner and must not run
    /// implicitly from an application process.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to migrate.</typeparam>
    /// <param name="app">The web application.</param>
    /// <param name="maxRetries">Maximum number of connection retry attempts (default: 50).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Obsolete(
        "Service startup migrations are disabled. Use Legacy.Maliev.AppHost.MigrationRunner with an approved schema baseline receipt.",
        DiagnosticId = "LEGACY001")]
    public static Task MigrateDatabaseAsync<TContext>(
        this IHost app,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        _ = app;
        _ = maxRetries;
        _ = cancellationToken;
        throw new InvalidOperationException(
            "Implicit service-startup database migration is disabled. "
            + "Run the isolated Legacy.Maliev.AppHost MigrationRunner with an approved schema baseline receipt.");
    }

    /// <summary>
    /// Ensures connection string has optimal pooling configuration optimized for low-spec nodes.
    /// Configured for n1-standard-1 (1 vCPU, 3.75GB RAM) with max 20 connections to conserve resources.
    /// </summary>
    private static string EnsureConnectionPooling(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

        // Optimize for low-spec nodes (n1-standard-1: 1 vCPU, 3.75GB RAM)
        // Lower connection pool to prevent resource exhaustion
        if (builder.MaxPoolSize == 100)
        {
            builder.MaxPoolSize = 20; // Reduced from 200 for low-spec nodes
        }
        if (builder.MinPoolSize == 0)
        {
            builder.MinPoolSize = 2; // Minimal warm connections to save memory
        }
        if (builder.ConnectionIdleLifetime == 300)
        {
            builder.ConnectionIdleLifetime = 60; // Recycle idle connections faster (1 minute)
        }
        if (builder.ConnectionPruningInterval == 10)
        {
            builder.ConnectionPruningInterval = 10; // Check for stale connections every 10 seconds
        }

        return builder.ConnectionString;
    }
}
