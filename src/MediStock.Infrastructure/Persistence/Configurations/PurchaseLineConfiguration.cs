using MediStock.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseLineConfiguration : IEntityTypeConfiguration<PurchaseLine>
{
    public void Configure(EntityTypeBuilder<PurchaseLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PurchaseLines", table =>
        {
            table.HasCheckConstraint(
                "CK_PurchaseLines_Quantity_Positive",
                "[Quantity] > 0");

            table.HasCheckConstraint(
                "CK_PurchaseLines_QuantityReceived_Within",
                "[QuantityReceived] >= 0 AND [QuantityReceived] <= [Quantity]");

            table.HasCheckConstraint(
                "CK_PurchaseLines_Costs_NonNegative",
                "[UnitCost] >= 0 AND [SellingPrice] >= 0");
        });

        builder.HasKey(line => line.Id);

        builder.Property(line => line.BatchNumber).HasMaxLength(PurchaseLine.BatchNumberMaxLength).IsRequired();

        // Quantities allow fractional dispensing (for example millilitres of syrup).
        builder.Property(line => line.Quantity).HasPrecision(18, 3).IsRequired();
        builder.Property(line => line.QuantityReceived).HasPrecision(18, 3).IsRequired();

        // Money is always decimal with two places; never a floating point type.
        builder.Property(line => line.UnitCost).HasPrecision(18, 2).IsRequired();
        builder.Property(line => line.SellingPrice).HasPrecision(18, 2).IsRequired();

        builder.Property(line => line.ExpiryDate).HasColumnType("date").IsRequired();

        builder.HasOne(line => line.Medicine)
            .WithMany()
            .HasForeignKey(line => line.MedicineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(line => line.MedicineBatch)
            .WithMany()
            .HasForeignKey(line => line.MedicineBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        // The domain rejects a second line for the same medicine and batch number; this is the same
        // rule expressed where it cannot be bypassed.
        builder.HasIndex(line => new { line.PurchaseId, line.MedicineId, line.BatchNumber })
            .IsUnique()
            .HasDatabaseName("UX_PurchaseLines_Purchase_Medicine_Batch");

        builder.HasIndex(line => line.MedicineId).HasDatabaseName("IX_PurchaseLines_MedicineId");
        builder.HasIndex(line => line.MedicineBatchId).HasDatabaseName("IX_PurchaseLines_MedicineBatchId");
    }
}
