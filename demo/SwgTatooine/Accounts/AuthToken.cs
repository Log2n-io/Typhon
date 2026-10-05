using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace SwgTatooine.Accounts;

/// <summary>
/// The opaque bearer token a successful <c>/auth</c> call returns and <c>Admit</c> validates (A2/A3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a JWT, deliberately.</b> A JWT would mean a header nobody reads, a JSON payload a client is tempted to parse, an
/// <c>alg</c> field that has been the source of a decade of authentication bypasses, and a library. What is actually needed is
/// "this server said this account id is good until this instant": sixteen bytes of payload and a MAC over them. Base64url of
/// <c>payload || mac</c>, one version byte in front so a format change is a refusal rather than a mis-parse.
/// </para>
/// <para>
/// <b>HMAC-SHA256, and the comparison is constant-time.</b> <see cref="CryptographicOperations.FixedTimeEquals"/>, because a
/// comparison that returns early tells an attacker how many leading bytes of the MAC were right, which is enough to forge one
/// byte at a time.
/// </para>
/// <para>
/// <b>The key, and what it costs when it is not set.</b> <c>SWG_AUTH_KEY</c> (base64 or hex, at least 32 bytes decoded) if
/// present; otherwise a random key for this process. A random key is the safe default — it is never weak and never shared —
/// and the price is stated rather than hidden: <b>every token stops working when the server restarts</b>, so a browser holding
/// one has to log in again. For an ephemeral AWS instance that is right. For the long-lived "prod" instance, set the variable.
/// </para>
/// <para>
/// <b>No revocation, and that is why the lifetime is short.</b> A bearer token this server does not store cannot be withdrawn;
/// the only bound on a stolen one is its expiry. <see cref="DefaultLifetime"/> is two hours — long enough for a session,
/// short enough that a leaked token is not a permanent key. A real revocation list belongs with the back office, not here.
/// </para>
/// </remarks>
public static class AuthToken
{
    /// <summary>The environment variable naming the signing key.</summary>
    public const string KeyVariable = "SWG_AUTH_KEY";

    /// <summary>How long a freshly minted token is good for.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(2);

    /// <summary>Format marker. A token minted by a different version is refused, not reinterpreted.</summary>
    private const byte Version = 1;

    /// <summary>Payload: 1 version + 8 entity id + 8 expiry (unix seconds).</summary>
    private const int PayloadBytes = 1 + 8 + 8;

    /// <summary>HMAC-SHA256 output.</summary>
    private const int MacBytes = 32;

    /// <summary>The shortest signing key accepted from the environment, bytes.</summary>
    public const int MinimumKeyBytes = 32;

    private static readonly (byte[] Bytes, bool FromEnvironment) Signing = ResolveKey();

    /// <summary>
    /// Whether the signing key came from <see cref="KeyVariable"/> rather than being generated for this process.
    /// </summary>
    /// <remarks>
    /// Read by the startup banner, so an operator can see at a glance whether tokens will survive a restart. It reports where
    /// the key came from and never what it is.
    /// </remarks>
    public static bool KeyIsPersistent => Signing.FromEnvironment;

    /// <summary>
    /// Mints a token for an account.
    /// </summary>
    /// <param name="account">The account the bearer is.</param>
    /// <param name="lifetime">How long it is good for; <see cref="DefaultLifetime"/> when not given.</param>
    /// <returns>The base64url token, safe to put in a header or a JSON body.</returns>
    public static string Mint(EntityId account, TimeSpan? lifetime = null)
    {
        var expiry = DateTimeOffset.UtcNow.Add(lifetime ?? DefaultLifetime).ToUnixTimeSeconds();

        Span<byte> token = stackalloc byte[PayloadBytes + MacBytes];
        token[0] = Version;
        BinaryPrimitives.WriteUInt64LittleEndian(token[1..], account.RawValue);
        BinaryPrimitives.WriteInt64LittleEndian(token[9..], expiry);
        HMACSHA256.HashData(Signing.Bytes, token[..PayloadBytes], token[PayloadBytes..]);

        return Base64Url.EncodeToString(token);
    }

    /// <summary>
    /// Validates a token and recovers the account it names.
    /// </summary>
    /// <param name="token">What the client presented. Anything at all, including <see langword="null"/> and rubbish.</param>
    /// <param name="account">The account on success; <c>default</c> otherwise.</param>
    /// <returns><see langword="true"/> when the token is well-formed, correctly signed and unexpired.</returns>
    /// <remarks>
    /// <b>One boolean for every kind of failure, on purpose.</b> Telling a caller "the signature was wrong" rather than "that
    /// expired" is a free hint to somebody probing the format. The server's own log can be specific; the wire cannot.
    /// </remarks>
    public static bool TryValidate(string token, out EntityId account)
    {
        account = default;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // A length bound before the decode, so a megabyte of base64 cannot be made into work. The encoded form of a token is 65 bytes of payload+MAC, which
        // base64url renders in 88 characters or fewer.
        if (token.Length > 128)
        {
            return false;
        }

        // IsValid FIRST, and this is not belt-and-braces. Base64Url.TryDecodeFromChars returns false only when the DESTINATION is too small; an invalid
        // character, bad padding or rubbish among the padding makes it THROW FormatException, documented and verified. A validator that throws on the input it
        // exists to reject would turn every malformed token into a 500 — and this one runs on the transport thread inside the admission hook, where an
        // exception is not somebody's HTTP error page.
        if (!Base64Url.IsValid(token.AsSpan(), out var decodedLength) || decodedLength != PayloadBytes + MacBytes)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[PayloadBytes + MacBytes];
        if (!Base64Url.TryDecodeFromChars(token, decoded, out var written) || written != decoded.Length)
        {
            return false;
        }

        if (decoded[0] != Version)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[MacBytes];
        HMACSHA256.HashData(Signing.Bytes, decoded[..PayloadBytes], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, decoded[PayloadBytes..]))
        {
            return false;
        }

        // Expiry is checked AFTER the MAC, which is the order that matters: reading an unauthenticated expiry and acting on it would be trusting a field an
        // attacker writes.
        var expiry = BinaryPrimitives.ReadInt64LittleEndian(decoded[9..]);
        if (expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        var raw = BinaryPrimitives.ReadUInt64LittleEndian(decoded[1..]);
        if (raw == 0)
        {
            return false;
        }

        account = EntityId.FromRawValue(raw);
        return true;
    }

    /// <summary>
    /// Reads the signing key from the environment, or makes one for this process.
    /// </summary>
    /// <returns>The key, and whether it was supplied rather than generated.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="KeyVariable"/> is set but is not decodable, or decodes to fewer than <see cref="MinimumKeyBytes"/> bytes.
    /// A key that was MEANT to be configured and is wrong must stop the server, not fall back to a random one — a silent
    /// fallback is how an operator ends up believing tokens survive a restart when they do not.
    /// </exception>
    private static (byte[] Bytes, bool FromEnvironment) ResolveKey()
    {
        var configured = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return (RandomNumberGenerator.GetBytes(MinimumKeyBytes), false);
        }

        var text = configured.Trim();
        byte[] key;
        try
        {
            key = text.Length % 2 == 0 && IsHex(text) ? Convert.FromHexString(text) : Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{KeyVariable} is set but is neither base64 nor hex. Generate one with: openssl rand -base64 32", ex);
        }

        if (key.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"{KeyVariable} decodes to {key.Length} bytes; at least {MinimumKeyBytes} are needed for HMAC-SHA256. "
                + "Generate one with: openssl rand -base64 32");
        }

        return (key, true);
    }

    /// <summary>Whether every character is a hex digit, which is what decides between the hex and base64 readings.</summary>
    /// <param name="text">The configured value.</param>
    /// <returns><see langword="true"/> when it is hex.</returns>
    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
