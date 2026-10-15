using MediStock.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class MedicineConfiguration : IEntityTypeConfiguration<Medicine>
{
    public void Configure(EntityTypeBuilder<Medicine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Medicines", table =>
        {
            table.HasCheckConstraint(
                "CK_Medicines_ReorderThreshold_NonNegative",
                "[ReorderThreshold] IS NULL OR [ReorderThreshold] >= 0");
            table.HasCheckConstraint(
                "CK_Medicines_TaxRate_Range",
                "[TaxRatePercent] >= 0 AND [TaxRatePercent] <= 100");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.DisplayName)
            .HasMaxLength(Medicine.DisplayNameMaxLength)
            .IsRequired();

        builder.Property(m => m.BrandName).HasMaxLength(Medicine.ShortTextMaxLength);
        builder.Property(m => m.GenericName).HasMaxLength(Medicine.ShortTextMaxLength);
        builder.Property(m => m.Strength).HasMaxLength(Medicine.ShortTextMaxLength);
        builder.Property(m => m.Manufacturer).HasMaxLength(Medicine.ShortTextMaxLength);
        builder.Property(m => m.Barcode).HasMaxLength(Medicine.BarcodeMaxLength);
        builder.Property(m => m.PackDescription).HasMaxLength(Medicine.PackDescriptionMaxLength);

        // Stored as an integer so the enum's meaning survives a rename.
        builder.Property(m => m.DosageForm)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(m => m.ReorderThreshold).HasPrecision(18, 3);

        // Same shape as SaleLines.TaxRatePercent: a percentage with two decimals, never money.
        builder.Property(m => m.TaxRatePercent)
            .HasPrecision(5, 2)
            .IsRequired();

        builder.Property(m => m.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(m => m.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(m => m.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        builder.HasIndex(m => m.DisplayName).HasDatabaseName("IX_Medicines_DisplayName");
        builder.HasIndex(m => m.GenericName).HasDatabaseName("IX_Medicines_GenericName");

        // One row per barcode, but many medicines may have no barcode at all.
        builder.HasIndex(m => m.Barcode)
            .IsUnique()
            .HasFilter("[Barcode] IS NOT NULL")
            .HasDatabaseName("UX_Medicines_Barcode");
    }
}
