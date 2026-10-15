using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SaleLineBatchAllocationConfiguration : IEntityTypeConfiguration<SaleLineBatchAllocation>
{
    public void Configure(EntityTypeBuilder<SaleLineBatchAllocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SaleLineBatchAllocations", table =>
        {
            table.HasCheckConstraint("CK_SaleLineBatchAllocations_Quantity_Positive", "[Quantity] > 0");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.BatchNumber)
            .HasMaxLength(MediStock.Domain.Inventory.MedicineBatch.BatchNumberMaxLength)
            .IsRequired();
        builder.Property(a => a.Quantity).HasPrecision(18, 3).IsRequired();
        builder.Property(a => a.ExpiryDate).HasColumnType("date").IsRequired();

        builder.HasOne(a => a.SaleLine)
            .WithMany(l => l.Allocations)
            .HasForeignKey(a => a.SaleLineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restricted, not cascaded: a batch is never deleted, and if one ever were, the invoice that
        // recorded taking stock from it must not quietly lose that fact.
        builder.HasOne(a => a.MedicineBatch)
            .WithMany()
            .HasForeignKey(a => a.MedicineBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line may draw on a given batch only once; two rows for the same batch would mean the
        // allocator produced a duplicate rather than consuming the batch in full.
        builder.HasIndex(a => new { a.SaleLineId, a.MedicineBatchId })
            .IsUnique()
            .HasDatabaseName("UX_SaleLineBatchAllocations_Line_Batch");

        // Answers "what has ever been sold out of this batch?" for expiry and recall investigations.
        builder.HasIndex(a => a.MedicineBatchId)
            .HasDatabaseName("IX_SaleLineBatchAllocations_MedicineBatchId");
    }
}
