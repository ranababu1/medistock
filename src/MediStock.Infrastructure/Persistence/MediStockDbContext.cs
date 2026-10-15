using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core context for the pharmacy database. Entity shapes are configured in
/// <c>Persistence/Configurations</c> so the domain types stay free of persistence attributes.
/// </summary>
public class MediStockDbContext : DbContext
{
    /// <summary>Key used for the connection string in <c>appsettings.json</c>.</summary>
    public const string ConnectionStringName = "MediStock";

    public const string SchemaName = "dbo";

    public MediStockDbContext(DbContextOptions<MediStockDbContext> options)
        : base(options)
    {
    }

    public DbSet<Medicine> Medicines => Set<Medicine>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<MedicineBatch> MedicineBatches => Set<MedicineBatch>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediStockDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
