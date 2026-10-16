using MediStock.Application.Identity;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Tests.Identity;

/// <summary>
/// The account editor's rules. Everything wrong with a draft is reported at once, because an administrator
/// typing a new account should not have to fix one thing, submit, and be told the next.
/// </summary>
public sealed class UserDraftTests
{
    private static UserDraft Draft(
        string userName = "anita",
        string displayName = "Anita Rao",
        Role role = Role.Cashier,
        string? password = "Marigold-Till-92",
        string? confirmPassword = "Marigold-Till-92",
        bool requiresPassword = true)
        => new()
        {
            UserName = userName,
            DisplayName = displayName,
            Role = role,
            Password = password,
            ConfirmPassword = confirmPassword,
            RequiresPassword = requiresPassword,
        };

    [Fact]
    public void An_empty_draft_names_every_missing_field_at_once()
    {
        var problems = UserDraft.Empty.Validate();

        Assert.Contains("An account name is required.", problems);
        Assert.Contains("A display name is required.", problems);
        Assert.Contains("A password is required for a new account.", problems);
        Assert.DoesNotContain("Choose a role.", problems);
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void A_complete_draft_has_no_problems()
    {
        Assert.Empty(Draft().Validate());
    }

    [Fact]
    public void An_account_name_longer_than_the_column_is_refused_before_the_database_sees_it()
    {
        var problems = Draft(userName: new string('a', User.UserNameMaxLength + 1)).Validate();

        Assert.Contains(problems, p => p.Contains("account name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_display_name_longer_than_the_column_is_refused_before_the_database_sees_it()
    {
        var problems = Draft(displayName: new string('a', User.DisplayNameMaxLength + 1)).Validate();

        Assert.Contains(problems, p => p.Contains("display name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_name_of_only_spaces_counts_as_missing()
    {
        var problems = Draft(userName: "   ", displayName: "   ").Validate();

        Assert.Contains("An account name is required.", problems);
        Assert.Contains("A display name is required.", problems);
    }

    [Fact]
    public void A_name_of_the_right_length_is_accepted_with_room_for_trailing_space()
    {
        // The length is measured after trimming, so a name pasted with a trailing space is not refused for
        // being one character too long.
        Assert.Empty(Draft(userName: new string('a', User.UserNameMaxLength) + "  ").Validate());
    }

    [Fact]
    public void Two_different_passwords_are_reported_as_a_mismatch()
    {
        var problems = Draft(password: "Marigold-Till-92", confirmPassword: "Marigold-Till-93").Validate();

        Assert.Contains("The two passwords do not match.", problems);
    }

    [Fact]
    public void A_weak_password_is_reported_with_every_reason()
    {
        var problems = Draft(password: "indi", confirmPassword: "indi").Validate();

        Assert.Contains(problems, p => p.Contains("10 characters", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("four different characters", StringComparison.Ordinal));
    }

    [Fact]
    public void A_password_containing_the_account_name_is_refused()
    {
        var problems = Draft(userName: "anita", password: "Anita-Marigold-92", confirmPassword: "Anita-Marigold-92")
            .Validate();

        Assert.Contains(
            problems,
            p => p.Contains("must not contain the account name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Editing_an_account_needs_no_password()
    {
        var draft = Draft(password: null, confirmPassword: null, requiresPassword: false);

        Assert.False(draft.HasPassword);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void A_new_account_with_no_password_is_refused()
    {
        var problems = Draft(password: null, confirmPassword: null, requiresPassword: true).Validate();

        Assert.Contains("A password is required for a new account.", problems);
    }

    [Fact]
    public void A_password_typed_only_in_the_confirmation_still_counts_as_supplied()
    {
        // Otherwise an administrator who typed the password into the wrong box would be told that no
        // password was supplied at all, which is not what happened.
        var problems = Draft(password: null, confirmPassword: "Marigold-Till-92").Validate();

        Assert.DoesNotContain("A password is required for a new account.", problems);
    }

    [Fact]
    public void A_role_outside_the_enum_is_refused()
    {
        var problems = Draft(role: (Role)99).Validate();

        Assert.Contains("Choose a role.", problems);
    }

    [Fact]
    public void Editing_an_account_carries_the_existing_values_and_does_not_demand_a_password()
    {
        var dto = new UserDto(7, "anita", "Anita Rao", Role.Cashier, true, DateTimeOffset.UnixEpoch, null);

        var draft = UserDraft.FromDto(dto);

        Assert.Equal("anita", draft.UserName);
        Assert.Equal("Anita Rao", draft.DisplayName);
        Assert.Equal(Role.Cashier, draft.Role);
        Assert.False(draft.RequiresPassword);
        Assert.Null(draft.Password);
        Assert.Empty(draft.Validate());
    }

    [Fact]
    public void Hashing_a_password_checks_the_draft_first_and_refuses_to_hash_a_bad_one()
    {
        var draft = Draft(password: "indi", confirmPassword: "indi");

        var thrown = Assert.Throws<DomainValidationException>(() => draft.HashPassword(new FakeHasher()));

        Assert.Contains("10 characters", thrown.Message);
    }

    [Fact]
    public void Hashing_a_valid_draft_hands_the_hasher_the_typed_password()
    {
        var hasher = new FakeHasher();

        var hash = Draft(password: "Marigold-Till-92").HashPassword(hasher);

        Assert.Equal("Marigold-Till-92", hasher.Seen);
        Assert.Equal(hash, hasher.Last);
    }

    [Fact]
    public void FromDto_refuses_a_null()
    {
        Assert.Throws<ArgumentNullException>(() => UserDraft.FromDto(null!));
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string? Seen { get; private set; }

        public PasswordHash? Last { get; private set; }

        public int Iterations => PasswordHash.MinimumIterations;

        public PasswordHash Hash(string password)
        {
            Seen = password;
            Last = PasswordHash.Create(password, Iterations);
            return Last;
        }

        public bool Verify(string? password, string? stored) => false;
    }
}
