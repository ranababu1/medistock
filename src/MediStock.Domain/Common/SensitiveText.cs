using System.Text.RegularExpressions;

namespace MediStock.Domain.Common;

/// <summary>
/// Removes secrets from text that is about to be written to a log, an audit row or a support screen.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately about the <em>shape</em> of a secret rather than about a list of the places
/// secrets appear: a key name followed by <c>=</c> or <c>:</c> and then a value. That catches a connection
/// string, a JSON settings file and a line an exception message happened to include, including one that a
/// later release adds and this one has never heard of.
/// </para>
/// <para>
/// The key is kept and the separator is rewritten as <c>=</c>, so the result still matches this pattern.
/// Scrubbing twice changes nothing further, but the output is not "clean" by
/// <see cref="LooksLikeItCarriesASecret"/>: that method answers whether there could be a credential there,
/// not whether one has already been dealt with.
/// </para>
/// <para>
/// Whitespace on its own is not a separator. "Password policy updated" and "the token has expired" have no
/// separator and no value, so they survive; redacting any sentence that merely contains one of these words
/// would destroy most of the trail.
/// </para>
/// </remarks>
public static partial class SensitiveText
{
    /// <summary>What a removed value is replaced with.</summary>
    public const string Redaction = "***";

    /// <summary>
    /// Returns <paramref name="text"/> with the value of anything that looks like a credential replaced
    /// by <see cref="Redaction"/>. Null and blank come back unchanged.
    /// </summary>
    public static string? Scrub(string? text)
        => string.IsNullOrEmpty(text) ? text : CredentialPattern().Replace(text, $"${{key}}={Redaction}");

    /// <summary>
    /// True when <paramref name="text"/> still appears to contain a credential. Used by tests and by the
    /// diagnostics screen to state whether a line is safe to show.
    /// </summary>
    public static bool LooksLikeItCarriesASecret(string? text)
        => !string.IsNullOrEmpty(text) && CredentialPattern().IsMatch(text);

    /// <summary>
    /// Shortens text to <paramref name="maxLength"/> characters, ending with an ellipsis when it had to
    /// cut, so a stored detail cannot grow without bound.
    /// </summary>
    public static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return maxLength <= 3 ? text[..maxLength] : string.Concat(text.AsSpan(0, maxLength - 1), "\u2026");
    }

    [GeneratedRegex(
        @"(?<key>\b(?:password|pwd|passwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|uid|user\s*id)\b)[""']?\s*(?:[=:]\s*)(""[^""]*""|'[^']*'|[^;\s,}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
