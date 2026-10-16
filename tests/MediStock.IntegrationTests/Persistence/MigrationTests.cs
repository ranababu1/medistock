using MediStock.Application.Diagnostics;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Persistence;

[Collection(TestDatabaseCollection.Name)]
public class MigrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public MigrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migrations_leave_no_pending_changes()
    {
        var pending = await _fixture.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MediStockDbContext>();
            return await db.Database.GetPendingMigrationsAsync();
        });

        Assert.Empty(pending);
    }

    [Fact]
    public async Task Health_check_reports_a_reachable_database_with_a_version()
    {
        var report = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IDatabaseHealthCheck>().CheckAsync());

        Assert.True(report.IsReachable, report.Error);
        Assert.Null(report.Error);
        Assert.Equal("MediStock_Test", report.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(report.ServerVersion));
        Assert.False(report.HasPendingMigrations);
        Assert.Contains("SqlServer", report.Provider, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Applying_migrations_again_is_a_no_op()
    {
        var applied = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IDatabaseHealthCheck>().ApplyPendingMigrationsAsync());

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task Every_expected_table_exists()
    {
        var tables = await _fixture.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MediStockDbContext>();
            var names = new List<string>();

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo'";

            await db.Database.OpenConnectionAsync();
            try
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        });

        Assert.Equal(
            new[]
            {
                "InvoiceSequences",
                "MedicineBatches",
                "Medicines",
                "Payments",
                "PurchaseLines",
                "Purchases",
                "SaleLineBatchAllocations",
                "SaleLines",
                "Sales",
                "SalesReturnLines",
                "SalesReturns",
                "StockMovements",
                "Suppliers",
                "__EFMigrationsHistory",
            },
            tables);
    }
}
