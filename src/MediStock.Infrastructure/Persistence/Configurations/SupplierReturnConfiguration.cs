using MediStock.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SupplierReturnConfiguration : IEntityTypeConfiguration<SupplierReturn>
{
    public void Configure(EntityTypeBuilder<SupplierReturn> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupplierReturns", table =>
        {
            table.HasCheckConstraint("CK_SupplierReturns_TotalCostValue_NonNegative", "[TotalCostValue] >= 0");

            // A return with no stated reason cannot be audited, and a blank one is always a mistake.
            table.HasCheckConstraint(
                "CK_SupplierReturns_Reason_NotBlank",
                "LEN(LTRIM(RTRIM([Reason]))) > 0");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ReturnNumber).HasMaxLength(SupplierReturn.ReturnNumberMaxLength).IsRequired();
        builder.Property(r => r.ReturnedBy).HasMaxLength(SupplierReturn.OperatorMaxLength).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(SupplierReturn.ReasonMaxLength).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(SupplierReturn.NotesMaxLength);
        builder.Property(r => r.SupplierCreditNoteNumber).HasMaxLength(SupplierReturn.CreditNoteMaxLength);
        builder.Property(r => r.TotalCostValue).HasPrecision(18, 2).IsRequired();

        builder.Property(r => r.BusinessDate).HasColumnType("date").IsRequired();
        builder.Property(r => r.ReturnedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        // The supplier account the credit lands on is never deleted from under the return. There is no
        // navigation either way: the supplier's name is copied onto the documents that need it, so this
        // is an identifier-only reference like the batch's own supplier reference.
        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.ReturnNumber)
            .IsUnique()
            .HasDatabaseName("UX_SupplierReturns_ReturnNumber");

        // Supplier credits are reported by business date, the same axis daily takings use.
        builder.HasIndex(r => r.BusinessDate).HasDatabaseName("IX_SupplierReturns_BusinessDate");
        builder.HasIndex(r => r.SupplierId).HasDatabaseName("IX_SupplierReturns_SupplierId");

        builder.HasMany(r => r.Lines)
            .WithOne(line => line.SupplierReturn!)
            .HasForeignKey(line => line.SupplierReturnId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}