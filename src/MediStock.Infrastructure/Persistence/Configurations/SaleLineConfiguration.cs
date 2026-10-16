using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SaleLines", table =>
        {
            table.HasCheckConstraint("CK_SaleLines_Quantity_Positive", "[Quantity] > 0");
            table.HasCheckConstraint(
                "CK_SaleLines_Money_NonNegative",
                "[UnitPrice] >= 0 AND [UnitCost] >= 0 AND [GrossAmount] >= 0 AND [DiscountAmount] >= 0"
                + " AND [TaxableAmount] >= 0 AND [TaxRatePercent] >= 0 AND [TaxAmount] >= 0 AND [LineTotal] >= 0");
            table.HasCheckConstraint("CK_SaleLines_TaxRate_Range", "[TaxRatePercent] <= 100");
            table.HasCheckConstraint("CK_SaleLines_Discount_Within_Gross", "[DiscountAmount] <= [GrossAmount]");
            table.HasCheckConstraint("CK_SaleLines_LineNumber_Positive", "[LineNumber] > 0");

            // What has been returned can never exceed what was sold, and never refunds more than was
            // charged. The domain checks this too; the constraint is what makes it true of the table.
            table.HasCheckConstraint(
                "CK_SaleLines_Returned_Within_Sold",
                "[ReturnedQuantity] >= 0 AND [ReturnedQuantity] <= [Quantity]");
            table.HasCheckConstraint(
                "CK_SaleLines_ReturnedAmount_Within_LineTotal",
                "[ReturnedAmount] >= 0 AND [ReturnedAmount] <= [LineTotal]");
            table.HasCheckConstraint(
                "CK_SaleLines_ReturnedTax_Within_Tax",
                "[ReturnedTax] >= 0 AND [ReturnedTax] <= [TaxAmount]");

            // LineCost is computed from the quantity and unit cost, so the constraint spells the
            // multiplication out rather than naming a column the table does not have. Rounding it the
            // same way the domain does keeps the two equal rather than merely close.
            table.HasCheckConstraint(
                "CK_SaleLines_ReturnedCost_Within_LineCost",
                "[ReturnedCost] >= 0 AND [ReturnedCost] <= ROUND([Quantity] * [UnitCost], 2)");

            // The stored money must agree with the pricing rules that produced it, so a hand-edited or
            // partially written line cannot look like a legitimate one.
            table.HasCheckConstraint(
                "CK_SaleLines_Amounts_Consistent",
                """
                [GrossAmount] = [TaxableAmount] + [DiscountAmount]
                AND [LineTotal] = [TaxableAmount] + [TaxAmount]
                """);
        });

        builder.HasKey(l => l.Id);

        // A return rewrites the returned quantities on the line, so the write is conditional: an operator
        // who refunds the last unit while another till is refunding the same unit matches no row and is
        // refused, instead of both returns committing and the shelf being credited twice. See ADR 0006
        // and ADR 0008.
        builder.Property<byte[]>(ConcurrencyToken.RowVersion).IsRowVersion();

        builder.Property(l => l.MedicineName).HasMaxLength(SaleLine.MedicineNameMaxLength).IsRequired();
        builder.Property(l => l.Quantity).HasPrecision(18, 3).IsRequired();
        builder.Property(l => l.ReturnedQuantity).HasPrecision(18, 3).IsRequired();

        foreach (var money in new[]
                 {
                     nameof(SaleLine.UnitPrice),
                     nameof(SaleLine.UnitCost),
                     nameof(SaleLine.GrossAmount),
                     nameof(SaleLine.DiscountAmount),
                     nameof(SaleLine.TaxableAmount),
                     nameof(SaleLine.TaxAmount),
                     nameof(SaleLine.LineTotal),
                     nameof(SaleLine.ReturnedAmount),
                     nameof(SaleLine.ReturnedTax),
                     nameof(SaleLine.ReturnedCost),
                 })
        {
            builder.Property<decimal>(money).HasPrecision(18, 2).IsRequired();
        }

        builder.Property(l => l.TaxRatePercent).HasPrecision(5, 2).IsRequired();

        builder.HasOne(l => l.Sale)
            .WithMany(s => s.Lines)
            .HasForeignKey(l => l.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        // The medicine is referenced for reporting, but never deleted: historic invoices must keep
        // pointing at the catalogue entry they were written against.
        builder.HasOne<MediStock.Domain.Catalogue.Medicine>()
            .WithMany()
            .HasForeignKey(l => l.MedicineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.SaleId, l.LineNumber })
            .IsUnique()
            .HasDatabaseName("UX_SaleLines_Sale_LineNumber");

        // Sales-by-medicine reporting.
        builder.HasIndex(l => l.MedicineId).HasDatabaseName("IX_SaleLines_MedicineId");

        builder.HasMany(l => l.Allocations)
            .WithOne(a => a.SaleLine!)
            .HasForeignKey(a => a.SaleLineId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(l => l.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
