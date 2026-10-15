using MediStock.Infrastructure;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediStock.IntegrationTests;

/// <summary>
/// Stands up a throwaway SQL Server database for the integration tests. The database is created
/// purely from EF Core migrations and dropped afterwards, so tests never touch the development or
/// any real pharmacy database.
/// </summary>
/// <remarks>
/// Set <c>MEDISTOCK_TEST_CONNECTION</c> to point the tests at another SQL Server instance. The
/// default is a LocalDB database named <c>MediStock_Test</c>.
/// </remarks>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "MEDISTOCK_TEST_CONNECTION";

    private const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=MediStock_Test;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True";

    private ServiceProvider? _provider;

    /// <summary>An absolute directory, so exported receipts never land in the tester's Documents folder.</summary>
    public static string ReceiptFolder { get; } =
        Path.Combine(Path.GetTempPath(), "MediStock-Test-Receipts");

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;

    /// <summary>The composition root under test, wired exactly as the desktop application wires it.</summary>
    public IServiceProvider Services => _provider
        ?? throw new InvalidOperationException("The test database has not been initialised yet.");

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MediStock"] = ConnectionString,
                ["MediStock:PharmacyName"] = "MediStock Test Pharmacy",
                ["MediStock:TimeZoneId"] = TimeZoneInfo.Local.Id,
                ["MediStock:ApplyMigrationsOnStartup"] = "false",
                ["MediStock:SeedDevelopmentData"] = "false",
                // Exported receipts are written here rather than into the tester's Documents folder.
                ["MediStock:ReceiptFolder"] = ReceiptFolder,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddMediStockInfrastructure(configuration);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MediStockDbContext>();

        // A fresh database created only from migrations is itself part of what is being verified.
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is null)
        {
            return;
        }

        try
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MediStockDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _provider.DisposeAsync();
            _provider = null;
        }
    }

    /// <summary>Runs <paramref name="work"/> inside a request scope, the way the application would.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }
}

/// <summary>Shares one database across every test in a collection.</summary>
[CollectionDefinition(Name)]
public sealed class TestDatabaseCollection : ICollectionFixture<TestDatabaseFixture>
{
    public const string Name = "SqlServer";
}
