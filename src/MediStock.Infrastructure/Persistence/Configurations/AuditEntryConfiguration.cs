using MediStock.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediStock.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The table is named AuditLogs after the plan's data model; the type is AuditEntry because it is one
        // row of the log, and a type called "AuditLog" would suggest it held the whole of it.
        builder.ToTable("AuditLogs", t =>
        {
            t.HasCheckConstraint(
                "CK_AuditLogs_Summary_NotEmpty",
                "LEN(LTRIM(RTRIM([Summary]))) > 0");
        });

        builder.HasKey(e => e.Id);

        // Not an identity column: the trail is written on the catalog's own context inside someone else's
        // transaction, so several rows can be staged for one save and the values are assigned by EF.
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.Action).HasConversion<int>().IsRequired();

        // The actor is stored as the name that was signed in with, so the row still reads correctly after a
        // rename, and as an identifier so it can still be joined back to the account. No foreign key: a row
        // must survive whatever happens to the account, and a refused sign-in has no account at all.
        builder.Property(e => e.Actor).HasMaxLength(AuditEntry.ActorMaxLength).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(AuditEntry.SummaryMaxLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength);
        builder.Property(e => e.EntityId).HasMaxLength(AuditEntry.EntityIdMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(AuditEntry.ReasonMaxLength);
        builder.Property(e => e.Detail).HasMaxLength(AuditEntry.DetailMaxLength);
        builder.Property(e => e.OccurredAtUtc).HasColumnType("datetimeoffset(3)").IsRequired();

        // The settings screen asks "newest first" on almost every visit, and "what happened to this invoice"
        // asks by action or record.
        builder.HasIndex(e => e.OccurredAtUtc).HasDatabaseName("IX_AuditLogs_OccurredAtUtc");
        builder.HasIndex(e => new { e.EntityType, e.EntityId }).HasDatabaseName("IX_AuditLogs_Entity");
        builder.HasIndex(e => e.Actor).HasDatabaseName("IX_AuditLogs_Actor");

        builder.Ignore(e => e.IsSensitive);
    }
}
