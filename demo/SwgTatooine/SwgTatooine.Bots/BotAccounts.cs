using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SwgTatooine.Bots;

/// <summary>What a bot posts to <c>/auth/signup</c> and <c>/auth/login</c>.</summary>
/// <param name="Name">The account name.</param>
/// <param name="Password">The derived password. Never logged, never echoed, never put in a URL.</param>
/// <remarks>
/// <b>Declared here rather than shared with the server's <c>AuthRequest</c>, and that is deliberate.</b> This project references
/// <c>Typhon.Client</c> and nothing else — 07-bots § 1's rule, so a bot cannot take a shortcut no browser has. The cost is this
/// duplicated shape; the benefit is that a bot proves the HTTP contract rather than sharing the server's idea of it. If the two
/// ever disagree, the bot fleet is what finds out, which is the right way round.
/// </remarks>
public sealed record BotAuthRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("password")] string Password);

/// <summary>What a successful <c>/auth</c> call returns.</summary>
/// <param name="Token">The bearer token to present on <c>HELLO</c>.</param>
/// <param name="Name">The normalised name.</param>
/// <param name="ExpiresInSeconds">How long the token is good for.</param>
public sealed record BotAuthResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds);

/// <summary>
/// One line of the accounts file: what a bot needs to log in again next time, and nothing more.
/// </summary>
/// <remarks>
/// <b>There is no password field, and there must never be one.</b> The password is derived from <c>SWG_BOT_KEY</c> and the index
/// every time it is needed (<see cref="BotCredentials.Password"/>), so a thousand live credentials never touch the disk: this file
/// can be copied to an instance, committed by accident or left in a bucket and it authorises nothing without the key, which lives
/// only in the environment. A file holding the passwords would be a thousand accounts lost to one <c>cat</c>.
/// <para>
/// <see cref="Resume"/> is base64 of the engine's 16-byte resume token — a per-session secret of the same weight as a token, which
/// is why the file is written user-only where the platform can express that (<c>BotAccountStore.Flush</c>).
/// </para>
/// </remarks>
public sealed record BotAccountRecord
{
    /// <summary>The bot's index in the population, which its name and password are derived from.</summary>
    [JsonPropertyName("i")]
    public int Index { get; init; }

    /// <summary>The account name as the server normalised it.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; }

    /// <summary>Whether a signup has already succeeded, so a later run goes straight to <c>/auth/login</c>.</summary>
    [JsonPropertyName("signedUp")]
    public bool SignedUp { get; init; }

    /// <summary>The last token minted for this account, or <see langword="null"/>.</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; }

    /// <summary>When that token expires, as a Unix second, so a run can plan a re-login rather than discover one.</summary>
    [JsonPropertyName("tokenExpUnix")]
    public long TokenExpUnix { get; init; }

    /// <summary>Base64 of the last resume token the server issued, or <see langword="null"/>.</summary>
    [JsonPropertyName("resume")]
    public string Resume { get; init; }
}

/// <summary>The source-generated serializer for the accounts file and the <c>/auth</c> bodies.</summary>
/// <remarks>
/// Source-generated rather than reflective so this project stays publishable self-contained or trimmed without a warning, which
/// matters because it is the artefact that goes on the bot box (I2).
/// </remarks>
[JsonSerializable(typeof(BotAccountRecord))]
[JsonSerializable(typeof(BotAuthRequest))]
[JsonSerializable(typeof(BotAuthResponse))]
public sealed partial class BotAccountJson : JsonSerializerContext
{
}

/// <summary>
/// A bot's name and password, both derived from its index so neither is ever stored.
/// </summary>
/// <remarks>
/// <b>Derived, not generated-and-saved.</b> A generated password has to be written down to be used twice, and a file of a thousand
/// passwords is a liability that grows with the fleet. A derivation needs one secret for the whole population: lose it and nothing
/// is exposed; rotate it and every account is orphaned at once, which is recoverable — the accounts file names them, and a fresh
/// file with a fresh key simply signs up a new population.
/// </remarks>
public static class BotCredentials
{
    /// <summary>The environment variable holding the fleet secret.</summary>
    public const string KeyVariable = "SWG_BOT_KEY";

    /// <summary>How many characters of the derived digest become the password. Comfortably inside the server's 8..256.</summary>
    public const int PasswordLength = 24;

    /// <summary>The shortest key accepted, so a placeholder cannot stand in for a secret.</summary>
    public const int MinimumKeyLength = 16;

    /// <summary>
    /// Reads the fleet key from the environment, or refuses to continue.
    /// </summary>
    /// <returns>The key.</returns>
    /// <exception cref="InvalidOperationException">No key, or one too short to be one.</exception>
    /// <remarks>
    /// <b>A hard refusal and not a generated default.</b> A default would make the fleet work on its first run and lock it out on
    /// the second, because the accounts the first run created would have passwords nothing could reproduce. Failing at startup with
    /// a sentence naming the variable costs one line in a run book; the alternative costs a thousand orphaned accounts.
    /// </remarks>
    public static string ReadKeyOrThrow()
    {
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                KeyVariable + " is not set. Bot passwords are derived from it and are never stored, so there is nothing to fall back on. Set it to at " +
                "least " + MinimumKeyLength.ToString(CultureInfo.InvariantCulture) + " characters, the SAME value on every run, or the accounts already " +
                "signed up become unusable.");
        }

        if (key.Length < MinimumKeyLength)
        {
            throw new InvalidOperationException(
                KeyVariable + " must be at least " + MinimumKeyLength.ToString(CultureInfo.InvariantCulture) + " characters; it is " +
                key.Length.ToString(CultureInfo.InvariantCulture) + ".");
        }

        return key;
    }

    /// <summary>The account name for a bot index: <c>bot000000</c>.</summary>
    /// <param name="index">The bot's index.</param>
    /// <returns>The name.</returns>
    /// <remarks>
    /// Six digits, zero-padded, after a letter — the server's rule is 3..24 characters starting with a letter over
    /// <c>[A-Za-z0-9_]</c>, and padding keeps the names sorting the way the indices do, which makes an accounts file readable.
    /// </remarks>
    public static string Name(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return string.Create(CultureInfo.InvariantCulture, $"bot{index:D6}");
    }

    /// <summary>
    /// The password for a bot index.
    /// </summary>
    /// <param name="key">The fleet key from <see cref="ReadKeyOrThrow"/>.</param>
    /// <param name="index">The bot's index.</param>
    /// <returns>A 24-character password, stable for as long as the key is.</returns>
    /// <remarks>
    /// HMAC-SHA256 over the bot's name, keyed by the fleet secret, rendered base64url and truncated. Twenty-four characters keep
    /// 144 bits, far past anything a password needs; the point of the construction is that it is a PRF — knowing one bot's password
    /// says nothing about another's, which a naive key-plus-index scheme would not give.
    /// </remarks>
    public static string Password(string key, int index)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        Span<byte> mac = stackalloc byte[32];
        var written = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Name(index)), mac);
        return Base64Url.EncodeToString(mac[..written])[..PasswordLength];
    }
}
