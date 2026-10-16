using MediStock.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SupplierReturnLineConfiguration : IEntityTypeConfiguration<SupplierReturnLine>
{
    public void Configure(EntityTypeBuilder<SupplierReturnLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupplierReturnLines", table =>
        {
            table.HasCheckConstraint("CK_SupplierReturnLines_LineNumber_Positive", "[LineNumber] > 0");
            table.HasCheckConstraint("CK_SupplierReturnLines_Quantity_Positive", "[Quantity] > 0");
            table.HasCheckConstraint("CK_SupplierReturnLines_CostValue_NonNegative", "[CostValue] >= 0 AND [UnitCost] >= 0");
        });

        builder.HasKey(line => line.Id);

        builder.Property(line => line.Quantity).HasPrecision(18, 3).IsRequired();
        builder.Property(line => line.UnitCost).HasPrecision(18, 2).IsRequired();
        builder.Property(line => line.CostValue).HasPrecision(18, 2).IsRequired();
        builder.Property(line => line.Notes).HasMaxLength(SupplierReturnLine.NotesMaxLength);

        builder.HasOne(line => line.SupplierReturn)
            .WithMany(supplierReturn => supplierReturn.Lines)
            .HasForeignKey(line => line.SupplierReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        // The batch the stock left is never removed from under the return: it is the evidence of which
        // quantities were credited, and the movement history points back at it.
        builder.HasOne(line => line.MedicineBatch)
            .WithMany()
            .HasForeignKey(line => line.MedicineBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        // One position per line on the document.
        builder.HasIndex(line => new { line.SupplierReturnId, line.LineNumber })
            .IsUnique()
            .HasDatabaseName("UX_SupplierReturnLines_Return_LineNumber");

        // Answers "how much of this batch has been sent back?" without walking the returns table.
        builder.HasIndex(line => line.MedicineBatchId).HasDatabaseName("IX_SupplierReturnLines_MedicineBatchId");
    }
}