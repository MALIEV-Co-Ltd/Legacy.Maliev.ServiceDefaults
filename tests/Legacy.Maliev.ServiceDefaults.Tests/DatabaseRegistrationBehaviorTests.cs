using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Legacy.Maliev.ServiceDefaults.Tests;

// Registration only: no database connection, command, schema or row access occurs.
public sealed class DatabaseRegistrationBehaviorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PostgresRegistration_PreservesCustomPoolAndTimeout_WithoutOpeningConnection(bool dynamicJson, bool customPool)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["ConnectionStrings:review"] = "Host=localhost;Database=synthetic;Username=synthetic" +
            (customPool ? ";Maximum Pool Size=7;Minimum Pool Size=1;Connection Idle Lifetime=35;Connection Pruning Interval=5" : "");
        builder.Configuration["Database:EnableDetailedErrors"] = "true";
        builder.AddPostgresDbContext<RegistrationContext>("review", dynamicJson,
            (_, options) => options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistrationContext>();
        var connection = context.Database.GetDbConnection();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(customPool ? 7 : 20, settings.MaxPoolSize);
        Assert.Equal(customPool ? 1 : 2, settings.MinPoolSize);
        Assert.Equal(customPool ? 35 : 60, settings.ConnectionIdleLifetime);
        Assert.Equal(customPool ? 5 : 10, settings.ConnectionPruningInterval);
        Assert.Equal(120, context.Database.GetCommandTimeout());
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal(dynamicJson, host.Services.GetService<NpgsqlDataSource>() is not null);
    }

    [Fact]
    public void PostgresRegistration_MissingConnection_RejectsWithoutLeakingOtherValues()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["ConnectionStrings:Other"] = "synthetic-private-value";
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddPostgresDbContext<RegistrationContext>());
        Assert.Contains("RegistrationContext", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Other", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresRegistration_CompatibilityOverload_AppliesCallerOptions()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["ConnectionStrings:RegistrationContext"] = "Host=localhost;Database=synthetic;Username=synthetic";
        builder.AddPostgresDbContext<RegistrationContext>(options => options.EnableDetailedErrors());
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistrationContext>();
        var options = context.GetService<IDbContextOptions>();
        Assert.True(options.FindExtension<CoreOptionsExtension>()!.DetailedErrorsEnabled);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task MigrationCompatibilityShim_RefusesStartupEvenWithoutDatabaseServices()
    {
        using var host = Host.CreateApplicationBuilder().Build();
#pragma warning disable LEGACY001
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.MigrateDatabaseAsync<RegistrationContext>());
#pragma warning restore LEGACY001
        Assert.Contains("Implicit service-startup database migration is disabled", exception.Message, StringComparison.Ordinal);
    }

    public sealed class RegistrationContext(DbContextOptions<RegistrationContext> options) : DbContext(options);
}
