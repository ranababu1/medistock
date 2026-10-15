using MediStock.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class MedicineBatchConfiguration : IEntityTypeConfiguration<MedicineBatch>
{
    public void Configure(EntityTypeBuilder<MedicineBatch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MedicineBatches", table =>
        {
            table.HasCheckConstraint(
                "CK_MedicineBatches_QuantityReceived_Positive",
                "[QuantityReceived] > 0");
            table.HasCheckConstraint(
                "CK_MedicineBatches_QuantityOnHand_NonNegative",
                "[QuantityOnHand] >= 0 AND [QuantityOnHand] <= [QuantityReceived]");
            table.HasCheckConstraint(
                "CK_MedicineBatches_Prices_NonNegative",
                "[PurchaseCost] >= 0 AND [SellingPrice] >= 0");
            table.HasCheckConstraint(
                "CK_MedicineBatches_ExpiryAfterReceipt",
                "[ExpiryDate] > [ReceivedOn]");
            table.HasCheckConstraint(
                "CK_MedicineBatches_Status_Valid",
                "[Status] BETWEEN 1 AND 5");
        });

        builder.HasKey(b => b.Id);

        builder.Property(b => b.BatchNumber).HasMaxLength(MedicineBatch.BatchNumberMaxLength).IsRequired();
        builder.Property(b => b.SupplierInvoiceNumber).HasMaxLength(MedicineBatch.InvoiceNumberMaxLength);
        builder.Property(b => b.StatusReason).HasMaxLength(StockMovement.ReasonMaxLength);

        // Quantities allow fractional dispensing (for example millilitres of syrup).
        builder.Property(b => b.QuantityReceived).HasPrecision(18, 3).IsRequired();
        builder.Property(b => b.QuantityOnHand).HasPrecision(18, 3).IsRequired();

        // Money is always decimal with two places; never a floating point type.
        builder.Property(b => b.PurchaseCost).HasPrecision(18, 2).IsRequired();
        builder.Property(b => b.SellingPrice).HasPrecision(18, 2).IsRequired();

        builder.Property(b => b.ExpiryDate).HasColumnType("date").IsRequired();
        builder.Property(b => b.ReceivedOn).HasColumnType("date").IsRequired();
        builder.Property(b => b.ReceivedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(b => b.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(b => b.Status).HasConversion<int>().IsRequired();

        builder.HasOne(b => b.Medicine)
            .WithMany()
            .HasForeignKey(b => b.MedicineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Supplier)
            .WithMany()
            .HasForeignKey(b => b.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // Drives the first-expiry-first-out allocation query.
        builder.HasIndex(b => new { b.MedicineId, b.ExpiryDate })
            .HasDatabaseName("IX_MedicineBatches_MedicineId_ExpiryDate");

        builder.HasIndex(b => b.ExpiryDate).HasDatabaseName("IX_MedicineBatches_ExpiryDate");
        builder.HasIndex(b => b.BatchNumber).HasDatabaseName("IX_MedicineBatches_BatchNumber");

        // Supports the duplicate-invoice warning during receiving.
        builder.HasIndex(b => new { b.SupplierId, b.SupplierInvoiceNumber })
            .HasDatabaseName("IX_MedicineBatches_SupplierId_Invoice");
    }
}
