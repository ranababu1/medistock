using MediStock.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Payments", table =>
        {
            table.HasCheckConstraint("CK_Payments_Amount_Positive", "[Amount] > 0");
            table.HasCheckConstraint("CK_Payments_Method_Valid", "[Method] BETWEEN 1 AND 4");
        });

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.Reference).HasMaxLength(Payment.ReferenceMaxLength);
        builder.Property(p => p.Method).HasConversion<int>().IsRequired();
        builder.Property(p => p.PaidAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        builder.HasOne(p => p.Sale)
            .WithMany(s => s.Payments)
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Takings by payment method.
        builder.HasIndex(p => new { p.SaleId, p.Method })
            .HasDatabaseName("IX_Payments_SaleId_Method");
    }
}
