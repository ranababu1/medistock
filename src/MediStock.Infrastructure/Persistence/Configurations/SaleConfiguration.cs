using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Sales", table =>
        {
            table.HasCheckConstraint("CK_Sales_Status_Valid", "[Status] BETWEEN 1 AND 3");

            // The stored totals must be the sum of the stored line values, not a fresh calculation
            // over them. A database that can hold an invoice whose lines do not add up to its total
            // cannot be trusted to reprint that invoice.
            table.HasCheckConstraint(
                "CK_Sales_Totals_Reconcile",
                """
                [SubTotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxableTotal] >= 0 AND [TaxTotal] >= 0
                AND [CostTotal] >= 0 AND [Total] >= 0
                AND [SubTotal] = [TaxableTotal] + [DiscountTotal]
                AND [Total] = [TaxableTotal] + [TaxTotal]
                AND [DiscountTotal] <= [SubTotal]
                """);

            table.HasCheckConstraint(
                "CK_Sales_Customer_Phone_With_Name",
                "[CustomerPhone] IS NULL OR [CustomerName] IS NOT NULL");

            // A cancelled invoice always records who cancelled it, when, and why; an uncancelled one
            // never does. This is what keeps a cancellation auditable rather than a status flag.
            table.HasCheckConstraint(
                "CK_Sales_Cancellation_Consistent",
                """
                ([Status] <> 3 AND [CancelledAtUtc] IS NULL AND [CancelledBy] IS NULL AND [CancellationReason] IS NULL)
                OR ([Status] = 3 AND [CancelledAtUtc] IS NOT NULL AND [CancelledBy] IS NOT NULL AND [CancellationReason] IS NOT NULL)
                """);
        });

        builder.HasKey(s => s.Id);

        // The status transition is a conditional write. Cancelling or marking an invoice returned
        // rewrites the whole row, so without this token two operators working on the same invoice
        // would each write their own status over a stale read. Mapped as a shadow property because
        // concurrency is a persistence concern and the aggregate has no reason to know about it.
        builder.Property<byte[]>(ConcurrencyToken.RowVersion).IsRowVersion();

        builder.Property(s => s.InvoiceNumber).HasMaxLength(Sale.InvoiceNumberMaxLength).IsRequired();
        builder.Property(s => s.SoldBy).HasMaxLength(Sale.OperatorMaxLength).IsRequired();
        builder.Property(s => s.CustomerName).HasMaxLength(Sale.CustomerNameMaxLength);
        builder.Property(s => s.CustomerPhone).HasMaxLength(Sale.CustomerPhoneMaxLength);
        builder.Property(s => s.Notes).HasMaxLength(Sale.NotesMaxLength);
        builder.Property(s => s.CancelledBy).HasMaxLength(Sale.OperatorMaxLength);
        builder.Property(s => s.CancellationReason).HasMaxLength(Sale.CancellationReasonMaxLength);

        foreach (var money in new[]
                 {
                     nameof(Sale.SubTotal),
                     nameof(Sale.DiscountTotal),
                     nameof(Sale.TaxableTotal),
                     nameof(Sale.TaxTotal),
                     nameof(Sale.Total),
                     nameof(Sale.CostTotal),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, 2).IsRequired();
        }

        builder.Property(s => s.Status).HasConversion<int>().IsRequired();
        builder.Property(s => s.BusinessDate).HasColumnType("date").IsRequired();
        builder.Property(s => s.SoldAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(s => s.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(s => s.CancelledAtUtc).HasColumnType("datetimeoffset(3)");

        // One invoice number identifies exactly one sale, for ever. The domain issues the number inside
        // the checkout transaction and this is the constraint that makes the promise true even if
        // another till issues one at the same instant.
        builder.HasIndex(s => s.InvoiceNumber)
            .IsUnique()
            .HasDatabaseName("UX_Sales_InvoiceNumber");

        // Daily takings and the sales history read by date.
        builder.HasIndex(s => s.BusinessDate).HasDatabaseName("IX_Sales_BusinessDate");
        builder.HasIndex(s => s.SoldAtUtc).HasDatabaseName("IX_Sales_SoldAtUtc");

        builder.HasMany(s => s.Lines)
            .WithOne(line => line.Sale!)
            .HasForeignKey(line => line.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Payments)
            .WithOne(payment => payment.Sale!)
            .HasForeignKey(payment => payment.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(s => s.Returns).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
