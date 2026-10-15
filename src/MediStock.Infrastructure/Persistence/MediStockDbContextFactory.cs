using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediStock.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without starting the WPF shell. The connection string is
/// read from the <c>MEDISTOCK_CONNECTION</c> environment variable, falling back to the same local
/// development database the desktop app uses. No credentials are stored in source control.
/// </summary>
public sealed class MediStockDbContextFactory : IDesignTimeDbContextFactory<MediStockDbContext>
{
    public const string ConnectionEnvironmentVariable = "MEDISTOCK_CONNECTION";

    internal const string DefaultDevelopmentConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=MediStock;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True";

    public MediStockDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultDevelopmentConnectionString;
        }

        var options = new DbContextOptionsBuilder<MediStockDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new MediStockDbContext(options);
    }
}
