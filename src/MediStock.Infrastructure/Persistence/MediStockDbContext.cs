using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
using MediStock.Domain.Sales;
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

    public DbSet<Purchase> Purchases => Set<Purchase>();

    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();

    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<SaleLine> SaleLines => Set<SaleLine>();

    public DbSet<SaleLineBatchAllocation> SaleLineBatchAllocations => Set<SaleLineBatchAllocation>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<SalesReturn> SalesReturns => Set<SalesReturn>();

    public DbSet<SalesReturnLine> SalesReturnLines => Set<SalesReturnLine>();

    public DbSet<SupplierReturn> SupplierReturns => Set<SupplierReturn>();

    public DbSet<SupplierReturnLine> SupplierReturnLines => Set<SupplierReturnLine>();

    public DbSet<InvoiceSequence> InvoiceSequences => Set<InvoiceSequence>();

    public DbSet<User> Users => Set<User>();

    /// <summary>
    /// The audit trail. Rows are only ever added, never updated or deleted, and a query that writes to it is
    /// a bug rather than a feature.
    /// </summary>
    public DbSet<AuditEntry> AuditLogs => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediStockDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
