using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InvoiceSequences", table =>
        {
            table.HasCheckConstraint("CK_InvoiceSequences_NextNumber_NonNegative", "[NextNumber] >= 0");
        });

        builder.HasKey(s => s.SequenceKey);

        builder.Property(s => s.SequenceKey)
            .HasMaxLength(InvoiceSequence.SequenceKeyMaxLength)
            .IsRequired();

        builder.Property(s => s.NextNumber).IsRequired();
        builder.Property(s => s.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        // No concurrency token here, deliberately. The token exists to stop a write that was decided
        // from a stale read; this row is never read first, it is advanced by one atomic statement that
        // takes an exclusive lock for the duration of the checkout transaction. A token would add a
        // second failure mode without closing a race the statement already closes.
    }
}
