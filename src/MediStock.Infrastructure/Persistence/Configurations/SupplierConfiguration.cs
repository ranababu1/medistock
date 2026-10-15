using MediStock.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Suppliers");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(Supplier.NameMaxLength).IsRequired();
        builder.Property(s => s.ContactPerson).HasMaxLength(Supplier.ContactPersonMaxLength);
        builder.Property(s => s.Phone).HasMaxLength(Supplier.PhoneMaxLength);
        builder.Property(s => s.Email).HasMaxLength(Supplier.EmailMaxLength);
        builder.Property(s => s.AddressLine).HasMaxLength(Supplier.AddressMaxLength);
        builder.Property(s => s.City).HasMaxLength(Supplier.CityMaxLength);
        builder.Property(s => s.State).HasMaxLength(Supplier.StateMaxLength);
        builder.Property(s => s.PostalCode).HasMaxLength(Supplier.PostalCodeMaxLength);
        builder.Property(s => s.Gstin).HasMaxLength(Supplier.GstinMaxLength);
        builder.Property(s => s.DrugLicenseNumber).HasMaxLength(Supplier.DrugLicenseMaxLength);
        builder.Property(s => s.Notes).HasMaxLength(Supplier.NotesMaxLength);

        builder.Property(s => s.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(s => s.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        builder.HasIndex(s => s.Name).IsUnique().HasDatabaseName("UX_Suppliers_Name");
        builder.HasIndex(s => s.Gstin)
            .IsUnique()
            .HasFilter("[Gstin] IS NOT NULL")
            .HasDatabaseName("UX_Suppliers_Gstin");
    }
}
