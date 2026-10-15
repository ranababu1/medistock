using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    /// <summary>
    /// Name of the shadow concurrency token. Named here so the migration, the model and any test that
    /// needs to talk about it all use one spelling.
    /// </summary>
    internal const string RowVersionProperty = "RowVersion";

    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Purchases", table =>
        {
            table.HasCheckConstraint(
                "CK_Purchases_Status_Valid",
                "[Status] BETWEEN 1 AND 4");

            // Draft and Received carry no closure details; Cancelled and Reversed always do, and
            // Cancelled can never look as though stock had been taken in. This is what makes the
            // header impossible to corrupt from outside the domain aggregate.
            table.HasCheckConstraint(
                "CK_Purchases_Lifecycle_Consistent",
                """
                ([Status] = 1 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL
                            AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)
                OR ([Status] = 2 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL
                            AND [ClosedAtUtc] IS NULL AND [ClosedBy] IS NULL AND [StatusReason] IS NULL)
                OR ([Status] = 4 AND [ReceivedOn] IS NULL AND [ReceivedAtUtc] IS NULL AND [ReceivedBy] IS NULL
                            AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)
                OR ([Status] = 3 AND [ReceivedOn] IS NOT NULL AND [ReceivedAtUtc] IS NOT NULL AND [ReceivedBy] IS NOT NULL
                            AND [ClosedAtUtc] IS NOT NULL AND [ClosedBy] IS NOT NULL AND [StatusReason] IS NOT NULL)
                """);
        });

        builder.HasKey(p => p.Id);

        // The lifecycle transition is a conditional write. EF appends this token to every UPDATE of the
        // header, so an operation that read the purchase before another one changed it fails at the
        // database instead of silently overwriting the newer state. Without it, two operators pressing
        // Receive at the same moment would each write their own status over a stale read and take the
        // same delivery into stock twice. Mapped as a shadow property because concurrency is a
        // persistence concern and the aggregate has no reason to know about it. See ADR 0006.
        builder.Property<byte[]>(RowVersionProperty).IsRowVersion();

        builder.Property(p => p.InvoiceNumber).HasMaxLength(Purchase.InvoiceNumberMaxLength).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(Purchase.NotesMaxLength);
        builder.Property(p => p.StatusReason).HasMaxLength(StockMovement.ReasonMaxLength);
        builder.Property(p => p.ReceivedBy).HasMaxLength(Purchase.ReceivedByMaxLength);
        builder.Property(p => p.ClosedBy).HasMaxLength(Purchase.ReceivedByMaxLength);

        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.InvoiceDate).HasColumnType("date").IsRequired();
        builder.Property(p => p.ReceivedOn).HasColumnType("date");
        builder.Property(p => p.ReceivedAtUtc).HasColumnType("datetimeoffset(3)");
        builder.Property(p => p.ClosedAtUtc).HasColumnType("datetimeoffset(3)");
        builder.Property(p => p.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(p => p.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        builder.HasOne(p => p.Supplier)
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Lines)
            .WithOne(line => line.Purchase!)
            .HasForeignKey(line => line.PurchaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Lines)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Supports the duplicate-invoice warning raised while receiving.
        builder.HasIndex(p => new { p.SupplierId, p.InvoiceNumber })
            .HasDatabaseName("IX_Purchases_SupplierId_InvoiceNumber");

        builder.HasIndex(p => p.InvoiceDate).HasDatabaseName("IX_Purchases_InvoiceDate");
        builder.HasIndex(p => p.Status).HasDatabaseName("IX_Purchases_Status");
    }
}
