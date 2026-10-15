using MediStock.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("StockMovements", table =>
        {
            table.HasCheckConstraint(
                "CK_StockMovements_QuantityDelta_NonZero",
                "[QuantityDelta] <> 0");
            table.HasCheckConstraint(
                "CK_StockMovements_QuantityAfter_NonNegative",
                "[QuantityAfter] >= 0");
            table.HasCheckConstraint(
                "CK_StockMovements_MovementType_Valid",
                "[MovementType] BETWEEN 1 AND 9");
            table.HasCheckConstraint(
                "CK_StockMovements_Reason_Required",
                "[MovementType] NOT IN (4, 5, 6, 8, 9) OR ([Reason] IS NOT NULL AND LEN([Reason]) > 0)");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.MovementType).HasConversion<int>().IsRequired();
        builder.Property(m => m.QuantityDelta).HasPrecision(18, 3).IsRequired();
        builder.Property(m => m.QuantityAfter).HasPrecision(18, 3).IsRequired();
        builder.Property(m => m.Reason).HasMaxLength(StockMovement.ReasonMaxLength);
        builder.Property(m => m.Reference).HasMaxLength(StockMovement.ReferenceMaxLength);
        builder.Property(m => m.PerformedBy).HasMaxLength(StockMovement.PerformedByMaxLength).IsRequired();
        builder.Property(m => m.OccurredAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(m => m.BusinessDate).HasColumnType("date").IsRequired();

        builder.HasOne(m => m.MedicineBatch)
            .WithMany()
            .HasForeignKey(m => m.MedicineBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.MedicineBatchId, m.OccurredAtUtc })
            .HasDatabaseName("IX_StockMovements_Batch_Occurred");

        builder.HasIndex(m => new { m.BusinessDate, m.MovementType })
            .HasDatabaseName("IX_StockMovements_BusinessDate_Type");

        builder.HasIndex(m => m.MedicineId).HasDatabaseName("IX_StockMovements_MedicineId");
    }
}
