using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// The password rules are the only thing standing between a shared counter and a guessable account, so the
/// boundaries are asserted exactly rather than approximately.
/// </summary>
public class PasswordPolicyTests
{
    [Fact]
    public void A_reasonable_password_is_accepted()
    {
        Assert.Empty(PasswordPolicy.Validate("Kestrel-Harbour-71", "admin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_password_is_refused_with_one_clear_sentence(string? password)
    {
        var problems = PasswordPolicy.Validate(password);

        Assert.Equal(["A password is required."], problems);
    }

    [Fact]
    public void The_shortest_accepted_password_is_exactly_the_documented_length()
    {
        // Two candidates a single character apart, so that only the length can be the reason one is refused.
        var justLongEnough = ShortestAccepted();
        var oneCharacterShort = justLongEnough[..^1];

        Assert.Equal(PasswordPolicy.MinimumLength, justLongEnough.Length);
        Assert.Equal(PasswordPolicy.MinimumLength - 1, oneCharacterShort.Length);

        Assert.Empty(PasswordPolicy.Validate(justLongEnough));

        Assert.Contains(
            $"The password must be at least {PasswordPolicy.MinimumLength} characters long.",
            PasswordPolicy.Validate(oneCharacterShort));
    }

    [Fact]
    public void The_longest_accepted_password_is_exactly_the_documented_length()
    {
        var longest = LongestAccepted();
        Assert.Equal(PasswordPolicy.MaximumLength, longest.Length);
        Assert.Empty(PasswordPolicy.Validate(longest));

        var oneTooMany = longest + "z";
        Assert.Contains(
            $"The password must be {PasswordPolicy.MaximumLength} characters or fewer.",
            PasswordPolicy.Validate(oneTooMany));
    }

    [Fact]
    public void A_password_built_from_too_few_different_characters_is_refused()
    {
        // Long enough, and not on the obvious list, but trivially guessable.
        Assert.Contains(
            "The password must use at least four different characters.",
            PasswordPolicy.Validate("aaaaaaaaaaab"));

        Assert.DoesNotContain(
            "The password must use at least four different characters.",
            PasswordPolicy.Validate("aaaaaaaaabcd"));
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]
    [InlineData("administrator")]
    [InlineData("letmein123")]
    public void An_obvious_password_is_refused_whatever_its_case(string password)
    {
        Assert.Contains(
            "That password is too easy to guess. Choose something else.",
            PasswordPolicy.Validate(password));
    }

    [Fact]
    public void An_obvious_word_inside_a_longer_password_is_not_by_itself_a_refusal()
    {
        // The check is on the whole password. Refusing every password that contains "welcome" would refuse
        // "Welcoming-Committee-9" as well, and a rule that tight is a rule that ends up on a sticky note.
        Assert.Empty(PasswordPolicy.Validate("Welcoming-Committee-9", "admin"));
    }

    [Fact]
    public void A_password_containing_the_account_name_is_refused()
    {
        Assert.Contains(
            "The password must not contain the account name.",
            PasswordPolicy.Validate("imran-the-pharmacist", "Imran"));
    }

    [Fact]
    public void A_two_character_account_name_is_not_checked_for_because_it_would_refuse_too_much()
    {
        // "ab" appears inside many perfectly good passwords; only a name long enough to be worth guessing is
        // worth refusing.
        Assert.DoesNotContain(
            "The password must not contain the account name.",
            PasswordPolicy.Validate("Kestrel-Ab-Harbour-71", "ab"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_one_per_attempt()
    {
        // Too short, too few different characters, and containing the account name. An operator who fixes
        // one problem only to be told about the next has to be told three times.
        var problems = PasswordPolicy.Validate("indiind", "indi");

        Assert.Contains($"The password must be at least {PasswordPolicy.MinimumLength} characters long.", problems);
        Assert.Contains("The password must use at least four different characters.", problems);
        Assert.Contains("The password must not contain the account name.", problems);
    }

    [Fact]
    public void Ensure_throws_the_same_problems_Validate_returns()
    {
        var refused = Assert.Throws<DomainValidationException>(() => PasswordPolicy.Ensure("short", "admin"));

        Assert.Contains("The password must be at least", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_accepts_a_password_that_Validate_accepts()
    {
        PasswordPolicy.Ensure("Kestrel-Harbour-71", "admin");
    }

    [Fact]
    public void Describe_states_both_bounds_so_the_screen_and_the_rule_cannot_disagree()
    {
        var described = PasswordPolicy.Describe();

        Assert.Contains(PasswordPolicy.MinimumLength.ToString(), described, StringComparison.Ordinal);
        Assert.Contains(PasswordPolicy.MaximumLength.ToString(), described, StringComparison.Ordinal);
    }

    /// <summary>A password of exactly the maximum length, using four or more different characters.</summary>
    private static string LongestAccepted()
    {
        var (prefix, suffix) = ("Kestrel", "-9!");
        return prefix + new string('a', PasswordPolicy.MaximumLength - prefix.Length - suffix.Length) + suffix;
    }

    /// <summary>A password of exactly the minimum length, using four or more different characters.</summary>
    private static string ShortestAccepted()
    {
        var (prefix, suffix) = ("Ab", "-9");
        return prefix + new string('x', PasswordPolicy.MinimumLength - prefix.Length - suffix.Length) + suffix;
    }
}
