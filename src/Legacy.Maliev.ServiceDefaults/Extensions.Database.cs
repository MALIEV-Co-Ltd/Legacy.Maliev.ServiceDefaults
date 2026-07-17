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
    /// Applies database migrations on application startup.
    /// Should be called after building the WebApplication.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type to migrate.</typeparam>
    /// <param name="app">The web application.</param>
    /// <param name="maxRetries">Maximum number of connection retry attempts (default: 50).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task MigrateDatabaseAsync<TContext>(
        this IHost app,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        // Do NOT impose a wall-clock timeout on the connectivity retry loop — with 30+
        // services starting simultaneously and a single PostgreSQL instance, even 300 s
        // is not guaranteed to be enough. The retry count (maxRetries) is the only bound
        // on the wait loop so the service will keep trying until PostgreSQL is ready.
        //
        // A separate per-operation timeout (120 s) guards MigrateAsync itself, which
        // should never hang for more than a couple of minutes once connectivity is established.

        const int defaultRetries = 50; // ≈ 50 × avg-10 s jitter = ~8 minutes maximum wait
        var retries = maxRetries ?? defaultRetries;

        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TContext>>();

        try
        {
            var strategy = dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                // ── Phase 1: wait for connectivity ──────────────────────────────────────
                // Use only the caller's cancellationToken here so the retry loop runs as
                // long as the host is alive (Aspire restart policy handles real failures).
                int retryCount = 0;
                var random = new Random();
                while (!await dbContext.Database.CanConnectAsync(cancellationToken))
                {
                    if (retryCount >= retries)
                    {
                        throw new InvalidOperationException(
                            $"Database connectivity check failed after {retries} attempts");
                    }

                    retryCount++;
                    // Jitter 5-15 s to mitigate thundering herd on 30+ service cold start
                    var delaySeconds = 5 + random.Next(0, 10);
                    logger.LogWarning(
                        "Waiting for database connectivity (attempt {Attempt}/{Max}). Retrying in {Delay}s...",
                        retryCount, retries, delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }

                // ── Phase 2: apply migrations (separate 120 s per-operation timeout) ───
                // MigrateAsync acquires an advisory lock in PostgreSQL and runs DDL. It
                // should complete well within 120 s for a normal schema. If it does not,
                // something is genuinely stuck and we want a clear error message.
                using var migrateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                migrateCts.CancelAfter(TimeSpan.FromSeconds(120));

                logger.LogInformation("Applying database migrations for {ContextType}", typeof(TContext).Name);
                try
                {
                    await dbContext.Database.MigrateAsync(migrateCts.Token);
                }
                catch (OperationCanceledException) when (migrateCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(
                        "MigrateAsync timed out after 120 seconds for {ContextType}. " +
                        "This may indicate a long-running migration or a stuck advisory lock.",
                        typeof(TContext).Name);
                    throw;
                }

                logger.LogInformation("Database migrations applied successfully for {ContextType}", typeof(TContext).Name);
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Re-throw; the inner catch already logged the timeout detail.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to apply database migrations for {ContextType}", typeof(TContext).Name);
            throw;
        }
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
