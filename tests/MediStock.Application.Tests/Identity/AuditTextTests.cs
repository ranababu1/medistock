using MediStock.Application.Identity;
using MediStock.Domain.Identity;

namespace MediStock.Application.Tests.Identity;

/// <summary>
/// The words the audit trail is read in. The point of these tests is that a new action cannot reach the
/// screen as a bare enum name, which is what an operator would see if somebody added an action and forgot
/// the label.
/// </summary>
public sealed class AuditTextTests
{
    public static TheoryData<AuditAction> EveryAction()
    {
        var data = new TheoryData<AuditAction>();
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            data.Add(action);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Every_action_has_words_of_its_own(AuditAction action)
    {
        var label = AuditText.Label(action);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual(action.ToString(), label);
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void No_label_is_the_name_of_another_action(AuditAction action)
    {
        // A label repeated across two actions would make the audit trail ambiguous exactly where it is being
        // read to settle an argument.
        var mine = AuditText.Label(action);

        var clashes = Enum.GetValues<AuditAction>()
            .Where(other => other != action && string.Equals(AuditText.Label(other), mine, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(clashes);
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void No_label_shouts_or_ends_in_a_full_stop(AuditAction action)
    {
        var label = AuditText.Label(action);

        Assert.DoesNotContain(".", label);
        Assert.DoesNotContain("_", label);
        Assert.Equal(label, label.Trim());
        Assert.NotEqual(label.ToUpperInvariant(), label);
    }

    [Fact]
    public void An_action_outside_the_enum_falls_back_to_its_name_rather_than_throwing()
    {
        // The trail is read on a support call. A row written by a newer release must still be listed.
        var label = AuditText.Label((AuditAction)9_999);

        Assert.Equal("9999", label);
    }

    [Theory]
    [InlineData(AuditAction.SignInSucceeded, "Signed in")]
    [InlineData(AuditAction.SignInFailed, "Sign-in refused")]
    [InlineData(AuditAction.SignOut, "Signed out")]
    [InlineData(AuditAction.SaleCancelled, "Sale cancelled")]
    [InlineData(AuditAction.BackupTaken, "Backup taken")]
    [InlineData(AuditAction.RestoreCompleted, "Backup restored")]
    [InlineData(AuditAction.PriceChanged, "Price changed")]
    [InlineData(AuditAction.PurchaseReversed, "Receipt reversed")]
    public void The_wording_an_operator_reads_is_the_one_that_was_agreed(AuditAction action, string expected)
    {
        // Pinned because these strings appear in a filter list and in the rows it filters; changing one is a
        // deliberate act, not an accident of editing.
        Assert.Equal(expected, AuditText.Label(action));
    }
}
