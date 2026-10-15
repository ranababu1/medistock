using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Dashboard;
using MediStock.Application.Diagnostics;
using MediStock.Infrastructure.Catalogue;
using MediStock.Infrastructure.Dashboard;
using MediStock.Infrastructure.Diagnostics;
using MediStock.Infrastructure.Persistence;
using MediStock.Infrastructure.Persistence.Seeding;
using MediStock.Infrastructure.Time;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Infrastructure;

/// <summary>
/// Composition root for the infrastructure layer. Kept as the single place that knows how the
/// application talks to SQL Server, so the desktop project only has to call one method.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Overrides the connection string, useful for tests and for pointing at a live server.</summary>
    public const string ConnectionStringOverrideVariable = MediStockDbContextFactory.ConnectionEnvironmentVariable;

    public static IServiceCollection AddMediStockInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<MediStockOptions>(configuration.GetSection(MediStockOptions.SectionName));

        // Read models take MediStockOptions directly rather than IOptions<T>; resolving the bound
        // instance here keeps a single source of truth for the settings.
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<MediStockOptions>>().Value);

        var connectionString = ResolveConnectionString(configuration);

        services.AddDbContext<MediStockDbContext>(options => options
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(MediStockDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
                sql.CommandTimeout(60);
            }));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IMedicineCatalog, EfMedicineCatalog>();
        services.AddScoped<IDashboardQueries, EfDashboardQueries>();
        services.AddScoped<IDatabaseHealthCheck, SqlServerDatabaseHealthCheck>();
        services.AddScoped<DevelopmentDataSeeder>();

        return services;
    }

    /// <summary>
    /// Prefers the <c>MEDISTOCK_CONNECTION</c> environment variable, then configuration, so a
    /// developer can point the app at a different instance without editing a tracked file.
    /// </summary>
    public static string ResolveConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringOverrideVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var fromConfiguration = configuration.GetConnectionString(MediStockDbContext.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(fromConfiguration))
        {
            throw new InvalidOperationException(
                $"No connection string configured. Set ConnectionStrings:{MediStockDbContext.ConnectionStringName} "
                + $"in appsettings.json, appsettings.Development.json, or the {ConnectionStringOverrideVariable} "
                + "environment variable.");
        }

        return fromConfiguration;
    }

    /// <summary>
    /// Logs the resolved target without its credentials, so a support log shows which database the
    /// application actually opened.
    /// </summary>
    public static void LogDatabaseTarget(ILogger logger, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            logger.LogInformation(
                "Database target: Server={Server}, Database={Database}, IntegratedSecurity={IntegratedSecurity}.",
                builder.DataSource,
                builder.InitialCatalog,
                builder.IntegratedSecurity);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("The configured connection string could not be parsed for logging.");
        }
    }
}
