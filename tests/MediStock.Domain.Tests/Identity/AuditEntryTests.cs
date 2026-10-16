using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// An audit row. What matters is that it can only be added to, that it always names somebody, and that the
/// free-text columns cannot be used to smuggle a credential into a file that an administrator reads and
/// emails to support.
/// </summary>
public class AuditEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_row_records_what_happened_who_did_it_and_when()
    {
        var entry = AuditEntry.Record(
            AuditAction.PriceChanged,
            "Paracetamol 500 mg moved from 20.00 to 22.50.",
            actor: "pharmacist",
            userId: 7,
            entityType: "Medicine",
            entityId: "41",
            reason: "Supplier raised the rate.",
            detail: "Was SellingPrice=20.00, now SellingPrice=22.50.",
            nowUtc: Now);

        Assert.Equal(AuditAction.PriceChanged, entry.Action);
        Assert.Equal("pharmacist", entry.Actor);
        Assert.Equal(7, entry.UserId);
        Assert.Equal("Paracetamol 500 mg moved from 20.00 to 22.50.", entry.Summary);
        Assert.Equal("Medicine", entry.EntityType);
        Assert.Equal("41", entry.EntityId);
        Assert.Equal("Supplier raised the rate.", entry.Reason);
        Assert.Equal("Was SellingPrice=20.00, now SellingPrice=22.50.", entry.Detail);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    [Fact]
    public void A_row_without_an_actor_is_recorded_against_the_system()
    {
        // A refused sign-in has no account to name. Recording nothing would leave the row unattributed and
        // make a run of refusals invisible.
        var entry = AuditEntry.Record(AuditAction.SignInFailed, "A sign-in was refused.", nowUtc: Now);

        Assert.Equal(AuditEntry.SystemActor, entry.Actor);
        Assert.Equal("system", entry.Actor);
        Assert.Null(entry.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_summary_is_refused_because_a_row_that_says_nothing_is_not_a_trail(string? summary)
    {
        Assert.Throws<DomainValidationException>(
            () => AuditEntry.Record(AuditAction.SaleCompleted, summary!));
    }

    [Fact]
    public void A_credential_in_any_free_text_column_is_replaced_before_it_is_stored()
    {
        var entry = AuditEntry.Record(
            AuditAction.BackupTaken,
            "Backup taken with Password=Harbour71 supplied.",
            actor: "admin",
            reason: "Connection string was Server=.;Database=MediStock;Password=Harbour71;",
            detail: "token: abcdef123456",
            nowUtc: Now);

        Assert.Equal("Backup taken with Password=*** supplied.", entry.Summary);
        Assert.Equal($"Connection string was Server=.;Database=MediStock;Password={SensitiveText.Redaction};", entry.Reason);
        Assert.Equal($"token={SensitiveText.Redaction}", entry.Detail);

        // The value itself is nowhere in the row, and the key stays behind on purpose so that an
        // administrator can still see what was being handled. What is asserted is the absence of
        // the value, not the absence of the shape.
        Assert.DoesNotContain("Harbour71", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Harbour71", entry.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_prose_that_merely_mentions_a_password_is_left_alone()
    {
        var entry = AuditEntry.Record(
            AuditAction.PasswordReset,
            "Password policy explained to the operator.",
            nowUtc: Now);

        Assert.Equal("Password policy explained to the operator.", entry.Summary);
    }

    [Fact]
    public void A_refusal_is_recorded_even_when_the_account_name_typed_is_longer_than_the_column()
    {
        // The name in this summary was typed by somebody who has not signed in. Refusing to record it, or
        // throwing on the way, would give that stranger a different answer from the one every other refusal
        // gets — which is exactly what the sign-in prompt must never do.
        var typed = new string('x', AuditEntry.SummaryMaxLength * 3);

        var entry = AuditEntry.Record(
            AuditAction.SignInFailed,
            $"A sign-in was refused for the account name \"{typed}\", which does not exist here.",
            nowUtc: Now);

        Assert.Equal(AuditEntry.SummaryMaxLength, entry.Summary.Length);
        Assert.StartsWith("A sign-in was refused for the account name", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_detail_is_both_scrubbed_and_truncated_rather_than_one_or_the_other()
    {
        var detail = "Password=Harbour71 " + new string('.', AuditEntry.DetailMaxLength * 2);

        var entry = AuditEntry.Record(
            AuditAction.BackupTaken,
            "Backup taken.",
            detail: detail,
            nowUtc: Now);

        Assert.Equal(AuditEntry.DetailMaxLength, entry.Detail!.Length);
        Assert.StartsWith($"Password={SensitiveText.Redaction} ", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Harbour", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_that_falls_beyond_the_cut_leaves_neither_its_key_nor_its_value_behind()
    {
        var entry = AuditEntry.Record(
            AuditAction.BackupTaken,
            "Backup taken.",
            detail: new string('.', AuditEntry.DetailMaxLength) + " Password=Harbour71",
            nowUtc: Now);

        Assert.Equal(AuditEntry.DetailMaxLength, entry.Detail!.Length);
        Assert.DoesNotContain("Password", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Harbour", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_free_text_column_is_trimmed_to_the_width_of_its_column()
    {
        var entry = AuditEntry.Record(
            AuditAction.SaleCompleted,
            new string('s', AuditEntry.SummaryMaxLength + 500),
            actor: new string('a', AuditEntry.ActorMaxLength + 500),
            entityType: new string('t', AuditEntry.EntityTypeMaxLength + 500),
            entityId: new string('i', AuditEntry.EntityIdMaxLength + 500),
            reason: new string('r', AuditEntry.ReasonMaxLength + 500),
            detail: new string('d', AuditEntry.DetailMaxLength + 500),
            nowUtc: Now);

        Assert.Equal(AuditEntry.SummaryMaxLength, entry.Summary.Length);
        Assert.Equal(AuditEntry.ActorMaxLength, entry.Actor.Length);
        Assert.Equal(AuditEntry.EntityTypeMaxLength, entry.EntityType!.Length);
        Assert.Equal(AuditEntry.EntityIdMaxLength, entry.EntityId!.Length);
        Assert.Equal(AuditEntry.ReasonMaxLength, entry.Reason!.Length);
        Assert.Equal(AuditEntry.DetailMaxLength, entry.Detail!.Length);
    }

    [Fact]
    public void A_truncated_column_is_marked_as_truncated_rather_than_merely_cut()
    {
        var entry = AuditEntry.Record(
            AuditAction.MedicineUpdated,
            new string('m', AuditEntry.SummaryMaxLength + 1),
            nowUtc: Now);

        Assert.EndsWith("\u2026", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_optional_columns_come_back_null_rather_than_as_empty_strings()
    {
        var entry = AuditEntry.Record(AuditAction.SignOut, "Ravi Menon signed out.", nowUtc: Now);

        Assert.Null(entry.EntityType);
        Assert.Null(entry.EntityId);
        Assert.Null(entry.Reason);
        Assert.Null(entry.Detail);
    }

    [Fact]
    public void A_blank_optional_column_is_stored_as_null_rather_than_as_whitespace()
    {
        var entry = AuditEntry.Record(
            AuditAction.SignOut,
            "Ravi Menon signed out.",
            entityType: "   ",
            reason: string.Empty,
            nowUtc: Now);

        Assert.Null(entry.EntityType);
        Assert.Null(entry.Reason);
    }

    [Theory]
    [InlineData(AuditAction.PriceChanged)]
    [InlineData(AuditAction.SaleCancelled)]
    [InlineData(AuditAction.PurchaseReversed)]
    [InlineData(AuditAction.ReturnRecorded)]
    [InlineData(AuditAction.RestoreCompleted)]
    [InlineData(AuditAction.UserRoleChanged)]
    public void An_action_a_support_call_is_likely_to_ask_about_is_marked_sensitive(AuditAction action)
    {
        Assert.True(AuditEntry.Record(action, "Something happened.", nowUtc: Now).IsSensitive);
    }

    [Theory]
    [InlineData(AuditAction.SignInSucceeded)]
    [InlineData(AuditAction.SignOut)]
    [InlineData(AuditAction.MedicineUpdated)]
    [InlineData(AuditAction.BackupTaken)]
    public void An_ordinary_action_is_not_marked_sensitive(AuditAction action)
    {
        Assert.False(AuditEntry.Record(action, "Something happened.", nowUtc: Now).IsSensitive);
    }

    [Fact]
    public void An_unknown_action_still_reads_as_something_rather_than_as_a_blank()
    {
        // The label table is exhaustive today; this proves the enum and the words cannot drift apart without
        // something visibly wrong on the settings screen.
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            Assert.False(string.IsNullOrWhiteSpace(action.ToString()));
        }
    }
}
