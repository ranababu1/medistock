using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SalesReturnConfiguration : IEntityTypeConfiguration<SalesReturn>
{
    public void Configure(EntityTypeBuilder<SalesReturn> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SalesReturns", table =>
        {
            table.HasCheckConstraint("CK_SalesReturns_RefundTotal_NonNegative", "[RefundTotal] >= 0");
            table.HasCheckConstraint("CK_SalesReturns_RefundMethod_Valid", "[RefundMethod] BETWEEN 1 AND 4");

            // A refund with no stated reason cannot be audited, and a blank one is always a mistake.
            table.HasCheckConstraint(
                "CK_SalesReturns_Reason_NotBlank",
                "LEN(LTRIM(RTRIM([Reason]))) > 0");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReturnNumber).HasMaxLength(SalesReturn.ReturnNumberMaxLength).IsRequired();
        builder.Property(r => r.ReturnedBy).HasMaxLength(SalesReturn.OperatorMaxLength).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(SalesReturn.ReasonMaxLength).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(SalesReturn.NotesMaxLength);
        builder.Property(r => r.RefundTotal).HasPrecision(18, 2).IsRequired();

        builder.Property(r => r.RefundMethod).HasConversion<int>().IsRequired();

        builder.Property(r => r.BusinessDate).HasColumnType("date").IsRequired();
        builder.Property(r => r.ReturnedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        // Restricted, not cascaded. Deleting an invoice must not be able to take the record of money
        // refunded against it along with it; the return has to be dealt with deliberately.
        builder.HasOne(r => r.Sale)
            .WithMany(s => s.Returns)
            .HasForeignKey(r => r.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.ReturnNumber)
            .IsUnique()
            .HasDatabaseName("UX_SalesReturns_ReturnNumber");

        // Returns are reported by business date, the same axis daily takings use.
        builder.HasIndex(r => r.BusinessDate).HasDatabaseName("IX_SalesReturns_BusinessDate");
        builder.HasIndex(r => r.SaleId).HasDatabaseName("IX_SalesReturns_SaleId");

        builder.HasMany(r => r.Lines)
            .WithOne(line => line.SalesReturn!)
            .HasForeignKey(line => line.SalesReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
