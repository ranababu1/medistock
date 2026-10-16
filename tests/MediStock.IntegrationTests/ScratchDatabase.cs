using MediStock.Infrastructure;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediStock.IntegrationTests;

/// <summary>
/// A database of its own, for the tests that have to break something the shared fixture could not survive.
/// </summary>
/// <remarks>
/// <para>
/// Two things cannot be tested against the database every other test is using. Setting up an installation
/// for the first time can only happen once per database, so the bootstrap cannot be re-run. And restoring
/// replaces the whole database with the contents of a file, which would throw away everything the rest of
/// the run depends on. Both get a database named for the purpose, created from the same migrations and
/// dropped when the test finishes.
/// </para>
/// <para>
/// The provider is wired exactly as the application wires it, and the first administrator is created
/// through the real bootstrap rather than by inserting rows, so what the test signs in as is an account the
/// application itself would have created.
/// </para>
/// </remarks>
public sealed class ScratchDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private ScratchDatabase(string name, ServiceProvider provider)
    {
        Name = name;
        _provider = provider;
    }

    /// <summary>The database name, so a test can report what it worked on.</summary>
    public string Name { get; }

    public IServiceProvider Services => _provider;

    /// <summary>
    /// Creates and migrates <c>MediStock_Test_&lt;purpose&gt;</c> on whichever server the tests are configured
    /// for, then hands it back empty.
    /// </summary>
    public static async Task<ScratchDatabase> CreateAsync(string purpose)
    {
        var name = $"MediStock_Test_{purpose}";
        var connection = TestDatabaseFixture.ConnectionStringFor(name);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MediStock"] = connection,
                ["MediStock:PharmacyName"] = $"MediStock Test Pharmacy ({purpose})",
                ["MediStock:TimeZoneId"] = TimeZoneInfo.Local.Id,
                ["MediStock:ApplyMigrationsOnStartup"] = "false",
                ["MediStock:SeedDevelopmentData"] = "false",
                ["MediStock:ReceiptFolder"] = TestDatabaseFixture.ReceiptFolder,
                ["MediStock:BackupFolder"] = TestDatabaseFixture.BackupFolder,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddMediStockInfrastructure(configuration);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        try
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MediStockDbContext>();

            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }

        return new ScratchDatabase(name, provider);
    }

    /// <summary>Runs <paramref name="work"/> inside a request scope, the way the application would.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MediStockDbContext>();

            // A restored database is dropped the same way a fresh one is: by name, from the catalog.
            SqlConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _provider.DisposeAsync();
        }
    }
}

/// <summary>
/// Creates one scratch database for a test class and drops it when the class is finished, so several tests
/// about the same fresh installation do not each pay for a database.
/// </summary>
public abstract class ScratchDatabaseFixture : IAsyncLifetime
{
    public ScratchDatabase Database { get; private set; } = null!;

    /// <summary>The database name suffix, which is also a short description of what the tests do to it.</summary>
    protected abstract string Purpose { get; }

    public async Task InitializeAsync() => Database = await ScratchDatabase.CreateAsync(Purpose);

    public async Task DisposeAsync() => await Database.DisposeAsync();
}

/// <summary>The scratch database for the first-run bootstrap tests.</summary>
public sealed class ScratchIdentityDatabase : ScratchDatabaseFixture
{
    protected override string Purpose => "Identity";
}

/// <summary>The scratch database for the restore round trip.</summary>
public sealed class ScratchRestoreDatabase : ScratchDatabaseFixture
{
    protected override string Purpose => "Restore";
}
