using System;

namespace SwgTatooine.Accounts;

/// <summary>
/// The rules an account name has to pass, and the single normalisation every path shares (A2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalisation exists because the index is byte-exact and people are not.</b> <c>Account.Name</c> is a unique
/// <c>[Index] String64</c>, so <c>Loic</c> and <c>loic</c> are two different keys and both signups would succeed — two
/// accounts a person cannot tell apart, which is an account-takeover vector dressed as a typo. Lower-casing before the key
/// is formed is what makes the index's uniqueness mean what a reader assumes it means.
/// </para>
/// <para>
/// <b>Invariant culture, named rather than defaulted.</b> <c>ToLower()</c> under a Turkish locale maps <c>I</c> to a
/// dotless <c>ı</c>, so the same name typed on two machines would hash to two keys and a login would fail on one of them.
/// This is the classic Turkish-I bug and the only defence is to say which culture decides.
/// </para>
/// <para>
/// <b>ASCII only, which is a deliberate narrowing and not an oversight.</b> Allowing the full Unicode range means
/// confusable names — Cyrillic <c>а</c> for Latin <c>a</c> — and defending against that needs a confusable table this demo
/// has no business carrying. A display name with any character somebody likes belongs on <c>Character</c> (A4), where
/// nothing unique depends on it.
/// </para>
/// </remarks>
public static class AccountName
{
    /// <summary>The shortest name accepted.</summary>
    public const int MinimumLength = 3;

    /// <summary>
    /// The longest name accepted.
    /// </summary>
    /// <remarks>
    /// Well under <c>String64</c>'s 63 usable bytes, so a name always round-trips through the key without truncation —
    /// which matters more than it looks: two names that differ only past the cut would become one key and one account.
    /// </remarks>
    public const int MaximumLength = 24;

    /// <summary>
    /// Normalises a name to the exact form stored and indexed, or reports why it cannot be.
    /// </summary>
    /// <param name="offered">What the person typed. May be <see langword="null"/>.</param>
    /// <param name="normalized">The lower-cased, trimmed name — the index key. Empty when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the name is acceptable.</returns>
    public static bool TryNormalize(string offered, out string normalized)
    {
        normalized = string.Empty;
        if (offered == null)
        {
            return false;
        }

        var trimmed = offered.Trim();
        if (trimmed.Length is < MinimumLength or > MaximumLength)
        {
            return false;
        }

        // A letter first, so a name can never be mistaken for a number or an id in a URL or a log line.
        if (!char.IsAsciiLetter(trimmed[0]))
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>Whether a name would be accepted, without producing the normalised form.</summary>
    /// <param name="offered">The candidate.</param>
    /// <returns><see langword="true"/> when acceptable.</returns>
    public static bool IsAcceptable(string offered) => TryNormalize(offered, out _);

    /// <summary>The rule, in the words an endpoint hands back to a client. One string, so the refusal cannot drift from the check.</summary>
    public const string Rule =
        "a name is 3 to 24 characters, starts with a letter, and uses only letters, digits and underscore";
}
