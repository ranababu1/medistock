using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using MediStock.Infrastructure;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
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

    /// <summary>
    /// Where the backup tests write. Absolute and empty to begin with, so a test can tell the file it just
    /// took from anything already there, and so nothing is written to the folder a pharmacy uses for real
    /// backups.
    /// </summary>
    public static string BackupFolder { get; } =
        Path.Combine(Path.GetTempPath(), "MediStock-Test-Backups");

    /// <summary>
    /// The account the integration tests act as. Almost every test is about a business rule rather than
    /// about permissions, so the fixture signs this administrator in for the whole run and the
    /// authorization tests sign in as somebody else to prove a refusal.
    /// </summary>
    public const string AdministratorUserName = "integration.admin";

    /// <summary>The administrator's password. Not a secret: it exists only inside a throwaway test database.</summary>
    public const string AdministratorPassword = "Tessellate-Meadow-42";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;

    /// <summary>
    /// The same server and the same credentials, pointed at another database. Used by the tests that need a
    /// database of their own, so that whatever instance the run is configured for is the instance they use.
    /// </summary>
    public static string ConnectionStringFor(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var builder = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
                ? configured
                : DefaultConnectionString)
        {
            InitialCatalog = database,
        };

        return builder.ConnectionString;
    }

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
                // Backups are written here for the same reason.
                ["MediStock:BackupFolder"] = BackupFolder,
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

        await SignInAsAdministratorAsync();
    }

    /// <summary>
    /// Creates the administrator this fixture runs as and signs them in, through the real first-run
    /// bootstrap, so the session the catalogues read is the one the application would have. Without it
    /// every catalogue call would be refused, which is the behaviour the authorization tests rely on.
    /// </summary>
    public async Task SignInAsAdministratorAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var result = await authentication.SetUpFirstAdministratorAsync(new UserDraft
        {
            UserName = AdministratorUserName,
            DisplayName = "Integration Administrator",
            Role = Role.Administrator,
            Password = AdministratorPassword,
            ConfirmPassword = AdministratorPassword,
            Reason = "Created by the integration test fixture.",
        });

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"The integration test fixture could not create its administrator: {result.Message}");
        }
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(BackupFolder))
        {
            Directory.Delete(BackupFolder, recursive: true);
        }

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

    /// <summary>The same scope, for work that needs nothing asynchronous — resolving a service, say.</summary>
    public T InScope<T>(Func<IServiceProvider, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        using var scope = Services.CreateScope();
        return work(scope.ServiceProvider);
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
