using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SalesReturnLineConfiguration : IEntityTypeConfiguration<SalesReturnLine>
{
    public void Configure(EntityTypeBuilder<SalesReturnLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SalesReturnLines", table =>
        {
            table.HasCheckConstraint("CK_SalesReturnLines_LineNumber_Positive", "[LineNumber] > 0");
            table.HasCheckConstraint("CK_SalesReturnLines_Quantity_Positive", "[Quantity] > 0");
            table.HasCheckConstraint("CK_SalesReturnLines_RefundAmount_NonNegative", "[RefundAmount] >= 0");
            table.HasCheckConstraint(
                "CK_SalesReturnLines_Refund_Components_NonNegative",
                "[TaxAmount] >= 0 AND [CostAmount] >= 0");
            table.HasCheckConstraint("CK_SalesReturnLines_Disposition_Valid", "[Disposition] BETWEEN 1 AND 3");
        });

        builder.HasKey(line => line.Id);

        builder.Property(line => line.Notes).HasMaxLength(SalesReturnLine.NotesMaxLength);
        builder.Property(line => line.Quantity).HasPrecision(18, 3).IsRequired();
        builder.Property(line => line.RefundAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(line => line.TaxAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(line => line.CostAmount).HasPrecision(18, 2).IsRequired();

        // Stored as an integer so the disposition cannot be silently reordered by inserting a value.
        builder.Property(line => line.Disposition).HasConversion<int>().IsRequired();

        builder.HasOne(line => line.SalesReturn)
            .WithMany(salesReturn => salesReturn.Lines)
            .HasForeignKey(line => line.SalesReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        // The invoice line the refund is credited against is never removed from under the return.
        builder.HasOne(line => line.SaleLine)
            .WithMany()
            .HasForeignKey(line => line.SaleLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(line => line.MedicineBatch)
            .WithMany()
            .HasForeignKey(line => line.MedicineBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        // One position per line on the document.
        builder.HasIndex(line => new { line.SalesReturnId, line.LineNumber })
            .IsUnique()
            .HasDatabaseName("UX_SalesReturnLines_Return_LineNumber");

        // Answers "what has come back off this invoice line?" and "how much of this batch has been
        // returned?" without walking the returns table.
        builder.HasIndex(line => line.SaleLineId).HasDatabaseName("IX_SalesReturnLines_SaleLineId");
        builder.HasIndex(line => line.MedicineBatchId).HasDatabaseName("IX_SalesReturnLines_MedicineBatchId");
    }
}
