using MediStock.Domain.Common;

namespace MediStock.Domain.Tests.Common;

/// <summary>
/// Scrubbing is the last line of defence between a connection string that an exception message happened to
/// quote and a log file that gets emailed to support, so both what it catches and what it leaves alone are
/// asserted.
/// </summary>
public class SensitiveTextTests
{
    [Theory]
    [InlineData("Password=Harbour71", "Password=***")]
    [InlineData("password = Harbour71", "password=***")]
    [InlineData("Pwd: hunter2", "Pwd=***")]
    [InlineData("Secret=abc", "Secret=***")]
    [InlineData("Token: deadbeef", "Token=***")]
    [InlineData("ApiKey=abc123", "ApiKey=***")]
    [InlineData("api-key=abc123", "api-key=***")]
    [InlineData("AccessKey=abc123", "AccessKey=***")]
    [InlineData("AccountKey=abc123", "AccountKey=***")]
    [InlineData("uid=sa", "uid=***")]
    [InlineData("User Id=sa", "User Id=***")]
    public void A_value_written_after_a_credential_key_is_removed(string text, string expected)
    {
        // The key is kept and the separator is normalised to a single "=", so a reader can still see which
        // credential was on the line without seeing its value.
        Assert.Equal(expected, SensitiveText.Scrub(text));
    }

    [Theory]
    [InlineData("password is written on the sticky note")]
    [InlineData("the token has expired")]
    [InlineData("share the secret with nobody")]
    public void A_key_with_no_separator_and_no_value_is_not_a_credential(string text)
    {
        // Whitespace on its own is not a separator: with no "=" or ":" there is nothing that could be a
        // value, and redacting the rest of the sentence would destroy the trail.
        Assert.Equal(text, SensitiveText.Scrub(text));
    }

    [Fact]
    public void The_credentials_are_removed_from_a_whole_connection_string_and_nothing_else_is()
    {
        const string line =
            "Server=(localdb)\\MSSQLLocalDB;Database=MediStock;Integrated Security=True;Password=Harbour71;TrustServerCertificate=True";

        var scrubbed = SensitiveText.Scrub(line);

        Assert.DoesNotContain("Harbour71", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Password=***", scrubbed, StringComparison.Ordinal);

        // Everything that was not a credential comes through exactly as it went in.
        Assert.Contains("Server=(localdb)\\MSSQLLocalDB", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Database=MediStock", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Integrated Security=True", scrubbed, StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate=True", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrubbing_again_changes_nothing_but_the_result_still_looks_like_a_credential()
    {
        // Worth stating plainly, because it is easy to get backwards: Scrub removes the value and leaves a
        // line whose shape still matches. LooksLikeItCarriesASecret therefore answers "could there be a
        // credential here", not "has this line been scrubbed", and no screen may treat it as the latter.
        var once = SensitiveText.Scrub("Password=Harbour71")!;

        Assert.Equal(once, SensitiveText.Scrub(once));
        Assert.True(SensitiveText.LooksLikeItCarriesASecret(once));
        Assert.DoesNotContain("Harbour71", once, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"Password\":\"Harbour71\"}", "{\"Password=***}")]
    [InlineData("{\"Pwd\":\"Harbour71\"}", "{\"Pwd=***}")]
    [InlineData("'Password' = 'Harbour71'", "'Password=***")]
    public void A_key_wrapped_in_quotes_as_a_settings_file_writes_it_is_still_recognised(string text, string expected)
    {
        // A settings file quotes both the key and the value, and a connection string in appsettings.json is
        // one of the likelier ways a credential reaches a log line. The key has to be allowed a closing
        // quote of its own for that shape to be seen at all.
        Assert.Equal(expected, SensitiveText.Scrub(text));
        Assert.DoesNotContain("Harbour71", SensitiveText.Scrub(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password policy updated.")]
    [InlineData("The operator forgot their password.")]
    [InlineData("Ravi Menon signed in.")]
    [InlineData("Medicine \"Paracetamol 500 mg\" created with the Pharmacist role.")]
    [InlineData("Paracetamol 500 mg moved from 20.00 to 22.50.")]
    [InlineData("A sale was cancelled because the customer changed their mind.")]
    [InlineData("The secret of a good audit trail is that nobody can edit it.")]
    public void Ordinary_prose_that_merely_mentions_a_credential_is_left_alone(string text)
    {
        // A rule that redacted any sentence containing the word "password" would redact most of the trail and
        // make it useless, so the rule is about the shape of a value and not about the word.
        Assert.Equal(text, SensitiveText.Scrub(text));
        Assert.False(SensitiveText.LooksLikeItCarriesASecret(text));
    }

    [Fact]
    public void Nothing_is_flagged_inside_a_message_that_has_no_credential_shape()
    {
        const string message = "An unexpected error occurred while saving the receipt.";

        Assert.False(SensitiveText.LooksLikeItCarriesASecret(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_text_comes_back_unchanged(string? text)
    {
        Assert.Equal(text, SensitiveText.Scrub(text));
        Assert.False(SensitiveText.LooksLikeItCarriesASecret(text));
    }

    [Fact]
    public void Short_text_is_returned_as_it_stands()
    {
        Assert.Equal("abcdef", SensitiveText.Truncate("abcdef", 6));
        Assert.Equal("", SensitiveText.Truncate("", 6));
        Assert.Null(SensitiveText.Truncate(null, 6));
    }

    [Fact]
    public void Text_over_the_limit_is_cut_and_marked_with_an_ellipsis()
    {
        var truncated = SensitiveText.Truncate("abcdefghij", 6);

        Assert.Equal(6, truncated!.Length);
        Assert.Equal("abcde\u2026", truncated);
    }

    [Fact]
    public void A_limit_too_small_for_an_ellipsis_simply_cuts()
    {
        Assert.Equal("abc", SensitiveText.Truncate("abcdefghij", 3));
        Assert.Equal("ab", SensitiveText.Truncate("abcdefghij", 2));
    }
}
