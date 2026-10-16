using MediStock.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users", t =>
        {
            // A role that is not one of the four known values would grant no permissions at sign-in, which
            // looks like a broken installation rather than a bad row.
            t.HasCheckConstraint(
                "CK_Users_Role_Defined",
                $"[Role] BETWEEN {(int)Role.Administrator} AND {(int)Role.InventoryOperator}");

            // An empty encoded hash can never match a password, but it would also make PasswordHash.Parse
            // throw while reading the row, so the shape is checked here as well.
            t.HasCheckConstraint(
                "CK_Users_PasswordHash_Encoded",
                "[PasswordHashEncoded] LIKE 'pbkdf2-sha256$%'");
        });

        builder.HasKey(u => u.Id);

        builder.Property(u => u.UserName).HasMaxLength(User.UserNameMaxLength).IsRequired();

        // The unique index is on the folded name, not the typed one, so that "Imran" and "imran" cannot
        // both exist. NormalisedUserName is set by the aggregate, never by a caller.
        builder.Property(u => u.NormalisedUserName).HasMaxLength(User.UserNameMaxLength).IsRequired();

        builder.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.Role).HasConversion<int>().IsRequired();
        builder.Property(u => u.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(u => u.PasswordHashEncoded).HasMaxLength(PasswordHash.EncodedMaxLength).IsRequired();

        builder.Property(u => u.CreatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(u => u.UpdatedAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();
        builder.Property(u => u.LastSignedInAtUtc).HasColumnType("datetimeoffset(3)");

        builder.HasIndex(u => u.NormalisedUserName).IsUnique().HasDatabaseName("UX_Users_NormalisedUserName");

        // Two administrators editing the same account — one renaming it while another resets its password —
        // would otherwise overwrite each other. Users are also the rows the last-administrator rule counts,
        // so the token doubles as the second line of defence behind the locked count in EfUserDirectory.
        builder.Property<byte[]>(ConcurrencyToken.RowVersion).IsRowVersion();

        // Permissions are derived from the role, so they are not a column.
        builder.Ignore(u => u.Permissions);
        builder.Ignore(u => u.Password);
        builder.Ignore(u => u.FriendlyName);
    }
}
