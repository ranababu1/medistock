using MediStock.Domain.Common;
using MediStock.Domain.Identity;

namespace MediStock.Domain.Tests.Identity;

/// <summary>
/// The stored form of a password. What matters is that a wrong password never matches, that a damaged or
/// weakened record is refused rather than trusted, and that the same password hashed twice produces two
/// different records.
/// </summary>
public class PasswordHashTests
{
    /// <summary>A deliberately cheap work factor, so the unit tests stay quick. Never used by the application.</summary>
    private const int FastIterations = PasswordHash.MinimumIterations;

    [Fact]
    public void A_password_matches_the_hash_it_was_created_from()
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.True(hash.Matches("Kestrel-Harbour-71"));
    }

    [Theory]
    [InlineData("Kestrel-Harbour-72")]
    [InlineData("Kestrel-Harbour-7")]
    [InlineData("kestrel-harbour-71")]
    [InlineData(" Kestrel-Harbour-71")]
    [InlineData("")]
    [InlineData(null)]
    public void A_different_password_does_not_match(string? candidate)
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.False(hash.Matches(candidate));
    }

    [Fact]
    public void The_same_password_hashed_twice_produces_two_different_records()
    {
        // The salt is fresh each time, so two operators who chose the same password do not share a hash and
        // a stolen table cannot be attacked one hash at a time.
        var first = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);
        var second = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.NotEqual(first.Encoded, second.Encoded);
        Assert.True(first.Matches("Kestrel-Harbour-71"));
        Assert.True(second.Matches("Kestrel-Harbour-71"));
    }

    [Fact]
    public void The_encoded_form_carries_the_algorithm_the_work_factor_the_salt_and_the_key()
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        var parts = hash.Encoded.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal(PasswordHash.AlgorithmName, parts[0]);
        Assert.Equal(FastIterations.ToString(), parts[1]);
        Assert.Equal(PasswordHash.SaltBytes, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(PasswordHash.KeyBytes, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void The_plaintext_password_is_nowhere_in_the_stored_record()
    {
        const string password = "Kestrel-Harbour-71";

        var hash = PasswordHash.Create(password, FastIterations);

        Assert.DoesNotContain(password, hash.Encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("Harbour", hash.Encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_record_round_trips_through_its_encoded_form()
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.True(PasswordHash.TryParse(hash.Encoded, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(hash.Encoded, parsed!.Encoded);
        Assert.Equal(hash, parsed);
        Assert.True(parsed.Matches("Kestrel-Harbour-71"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plaintext")]
    [InlineData("pbkdf2-sha256$210000$onlythreeparts")]
    [InlineData("pbkdf2-sha256$210000$notbase64!$alsonotbase64!")]
    [InlineData("bcrypt$210000$c2FsdA==$a2V5")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$a2V5")]
    [InlineData("pbkdf2-sha256$10000$$")]
    public void A_record_that_cannot_be_trusted_is_refused_rather_than_thrown_over(string? encoded)
    {
        Assert.False(PasswordHash.TryParse(encoded, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void A_record_whose_work_factor_was_edited_downwards_is_treated_as_damaged()
    {
        // A cheaper hash is a weaker hash. Accepting it would let anyone with write access to the table
        // quietly reduce the cost of guessing everybody's password.
        var weakened = $"pbkdf2-sha256${PasswordHash.MinimumIterations - 1}$c2FsdA==$a2V5";

        Assert.False(PasswordHash.TryParse(weakened, out _));
    }

    [Fact]
    public void A_record_longer_than_the_cap_is_refused_before_it_is_parsed()
    {
        var huge = $"pbkdf2-sha256${FastIterations}$" + new string('A', PasswordHash.EncodedMaxLength);

        Assert.False(PasswordHash.TryParse(huge, out _));
    }

    [Fact]
    public void Parse_throws_the_same_refusal_that_TryParse_reports()
    {
        var refused = Assert.Throws<DomainValidationException>(() => PasswordHash.Parse("plaintext"));

        Assert.Equal("The stored password is not in a recognised format.", refused.Message);
    }

    [Fact]
    public void Creating_a_hash_refuses_a_blank_password()
    {
        Assert.Throws<DomainValidationException>(() => PasswordHash.Create(string.Empty));
        Assert.Throws<DomainValidationException>(() => PasswordHash.Create(string.Empty, FastIterations));
    }

    [Fact]
    public void Creating_a_hash_refuses_a_work_factor_weaker_than_the_floor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PasswordHash.Create("Kestrel-Harbour-71", PasswordHash.MinimumIterations - 1));
    }

    [Fact]
    public void A_hash_written_at_the_current_work_factor_does_not_need_rehashing()
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", PasswordHash.RecommendedIterations);

        Assert.False(hash.NeedsRehash);
    }

    [Fact]
    public void A_hash_written_at_an_older_cheaper_work_factor_is_still_read_and_is_flagged_for_rehashing()
    {
        // This is what makes raising RecommendedIterations in a later release safe: the passwords written by
        // the earlier one keep working and are upgraded the next time their owner signs in.
        var old = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.True(old.NeedsRehash);
        Assert.True(old.Matches("Kestrel-Harbour-71"));
    }

    [Fact]
    public void The_stored_form_is_the_only_way_the_record_describes_itself()
    {
        var hash = PasswordHash.Create("Kestrel-Harbour-71", FastIterations);

        Assert.Equal(hash.Encoded, hash.ToString());
    }
}
