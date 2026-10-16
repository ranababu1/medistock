using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Dashboard;
using MediStock.Application.Diagnostics;
using MediStock.Application.Identity;
using MediStock.Application.Identity.Authorization;
using MediStock.Application.Inventory;
using MediStock.Application.Operations;
using MediStock.Application.Purchasing;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Infrastructure.Catalogue;
using MediStock.Infrastructure.Dashboard;
using MediStock.Infrastructure.Diagnostics;
using MediStock.Infrastructure.Identity;
using MediStock.Infrastructure.Inventory;
using MediStock.Infrastructure.Operations;
using MediStock.Infrastructure.Persistence;
using MediStock.Infrastructure.Persistence.Seeding;
using MediStock.Infrastructure.Purchasing;
using MediStock.Infrastructure.Reporting;
using MediStock.Infrastructure.Sales;
using MediStock.Infrastructure.Suppliers;
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

        // A desktop application has no request boundary, so every catalog would otherwise share one
        // context for the life of the process and overlapping async work would collide on it. The
        // factory hands each operation its own context instead.
        services.AddDbContextFactory<MediStockDbContext>(options => ConfigureDatabase(options, connectionString));

        // Kept so one context can still be resolved when a caller owns it explicitly — migrations, the
        // development seeder and the integration tests. The caller's scope owns and disposes it.
        services.AddScoped(provider => provider
            .GetRequiredService<IDbContextFactory<MediStockDbContext>>()
            .CreateDbContext());

        services.AddSingleton<IClock, SystemClock>();

        // The session is a singleton and is written only by the authentication service, which is the one
        // place that knows how somebody becomes signed in. Everything else takes IUserSession.
        services.AddSingleton<UserSession>();
        services.AddSingleton<IUserSession>(provider => provider.GetRequiredService<UserSession>());

        services.AddSingleton<IPasswordHasher>(provider => new Pbkdf2PasswordHasher(
            provider.GetRequiredService<MediStockOptions>().PasswordIterations));

        services.AddScoped<IAuthenticationService, EfAuthenticationService>();
        services.AddScoped<IUserDirectory, EfUserDirectory>();
        services.AddScoped<IAuditTrail, EfAuditTrail>();
        services.AddScoped<IBackupService, SqlServerBackupService>();

        // Each catalog is registered wrapped in the permission guard that belongs to its contract, so a screen
        // cannot reach an unguarded catalog by resolving it a different way. See ADR 0009.
        services.AddGuardedCatalog<IMedicineCatalog, EfMedicineCatalog>();
        services.AddGuardedCatalog<ISupplierCatalog, EfSupplierCatalog>();
        services.AddGuardedCatalog<IPurchaseCatalog, EfPurchaseCatalog>();
        services.AddGuardedCatalog<ISaleCatalog, EfSaleCatalog>();
        services.AddGuardedCatalog<ISaleReturnCatalog, EfSaleReturnCatalog>();
        services.AddGuardedCatalog<ISupplierReturnCatalog, EfSupplierReturnCatalog>();
        services.AddGuardedCatalog<IInventoryOperations, EfInventoryOperations>();
        services.AddGuardedCatalog<IReportQueries, EfReportQueries>();
        services.AddGuardedCatalog<ICsvExporter, CsvReportExporter>();

        // Reads and exports that carry no permission of their own.
        services.AddScoped<IStockQueries, EfStockQueries>();
        services.AddScoped<ISalesQueries, EfSalesQueries>();
        services.AddScoped<IReceiptExporter, TextReceiptExporter>();
        services.AddScoped<EfInvoiceNumberAllocator>();
        services.AddScoped<IDashboardQueries, EfDashboardQueries>();
        services.AddScoped<IDatabaseHealthCheck, SqlServerDatabaseHealthCheck>();
        services.AddScoped<DevelopmentDataSeeder>();

        return services;
    }

    /// <summary>
    /// Single place that decides how a context talks to SQL Server, so a context obtained from the
    /// factory and one resolved directly behave identically.
    /// </summary>
    /// <remarks>
    /// Multiple active result sets is switched off even when the configured connection string asks for it.
    /// With MARS on, SQL Server refuses savepoints, and EF Core then cannot roll a partly-applied
    /// <c>SaveChanges</c> back to a clean state inside a transaction: a batch that fails half way leaves the
    /// rows it managed to write behind. Several operations now write more than one batch per transaction,
    /// because an audited action stages its audit row and saves it in the same transaction as the thing it
    /// describes. Entity Framework does not need MARS; it buffers what it reads.
    /// </remarks>
    private static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString)
        => options.UseSqlServer(WithoutMultipleActiveResultSets(connectionString), sql =>
        {
            sql.MigrationsAssembly(typeof(MediStockDbContext).Assembly.FullName);
            sql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
            sql.CommandTimeout(60);
        });

    /// <summary>Clears <c>MultipleActiveResultSets</c> from a connection string, if it is set.</summary>
    private static string WithoutMultipleActiveResultSets(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.MultipleActiveResultSets)
        {
            return connectionString;
        }

        builder.MultipleActiveResultSets = false;

        return builder.ConnectionString;
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
