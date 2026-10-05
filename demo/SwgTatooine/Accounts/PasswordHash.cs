using System;
using System.Security.Cryptography;
using System.Text;

namespace SwgTatooine.Accounts;

/// <summary>
/// Hashes and verifies an account password (A1).
/// </summary>
/// <remarks>
/// <para>
/// <b>PBKDF2-SHA256 rather than Argon2id, and the choice is the dependency rather than the cryptography.</b> Argon2id is
/// the better KDF and costs a NuGet package in a blueprint whose whole point is that it uses nothing a reader would have
/// to go and find. <c>Rfc2898DeriveBytes</c> is in the BCL, and the parameters are stored beside the hash so the cost can
/// be raised later without invalidating anybody — which is the property that actually matters, since a KDF choice made
/// once and frozen is the failure mode here.
/// </para>
/// <para>
/// <b>"It is only a demo" is a bad argument in this one place.</b> It is the one surface a visitor judges on defaults, and
/// getting it right costs an afternoon: a per-account random salt, a work factor that is written down rather than implied,
/// a constant-time comparison, and a password that is never stored, logged or put in a URL.
/// </para>
/// <para>
/// <b>What is deliberately absent</b> ([11 § 1.3](../../../claude/design/SwgTatooine/11-back-office.md)): no password
/// reset, no lockout policy beyond the rate limits the command classes already provide, and no way for an operator to read
/// or set a password — the back office can only delete an account.
/// </para>
/// </remarks>
public static class PasswordHash
{
    /// <summary>Length of the derived key, bytes. SHA-256's own output size; a longer key buys nothing here.</summary>
    public const int HashBytes = 32;

    /// <summary>Length of the per-account salt, bytes.</summary>
    /// <remarks>
    /// Sixteen is the usual floor and is ample: the salt's job is to make one precomputed table useless against a whole
    /// database, and 128 bits of it means no two accounts ever share one even across every deployment of this demo.
    /// </remarks>
    public const int SaltBytes = 16;

    /// <summary>
    /// Work factor for new hashes. Stored with every hash, so raising this does not invalidate existing accounts.
    /// </summary>
    /// <remarks>
    /// OWASP's 2023 floor for PBKDF2-SHA256 is 600 000; this is that. It costs roughly a few hundred microseconds of one
    /// core per login, which is paid on a Kestrel thread and never on the tick — the number is allowed to be large
    /// precisely because it is nowhere near the simulation.
    /// </remarks>
    public const int DefaultIterations = 600_000;

    /// <summary>The shortest password accepted.</summary>
    /// <remarks>
    /// Eight, and no composition rules. A length floor is the one requirement that measurably helps; "must contain a
    /// symbol" produces <c>Password1!</c> and a sticky note.
    /// </remarks>
    public const int MinimumPasswordLength = 8;

    /// <summary>The longest password accepted.</summary>
    /// <remarks>
    /// A bound exists so that a long input cannot turn the KDF into a denial of service against the auth endpoint. 256 is
    /// far above anything a person types and far below anything that costs real time.
    /// </remarks>
    public const int MaximumPasswordLength = 256;

    /// <summary>Derives a hash and a fresh salt for a new or changed password.</summary>
    /// <param name="password">The plaintext, which this method does not retain.</param>
    /// <param name="hash">Receives <see cref="HashBytes"/> bytes.</param>
    /// <param name="salt">Receives <see cref="SaltBytes"/> bytes.</param>
    /// <param name="iterations">Receives the work factor used, to be stored with the hash.</param>
    /// <exception cref="ArgumentException">The password is outside the accepted length range.</exception>
    public static void Create(string password, Span<byte> hash, Span<byte> salt, out int iterations)
    {
        Validate(password);
        if (hash.Length != HashBytes)
        {
            throw new ArgumentException($"hash must be {HashBytes} bytes", nameof(hash));
        }

        if (salt.Length != SaltBytes)
        {
            throw new ArgumentException($"salt must be {SaltBytes} bytes", nameof(salt));
        }

        RandomNumberGenerator.Fill(salt);
        iterations = DefaultIterations;
        Derive(password, salt, iterations, hash);
    }

    /// <summary>Checks a password against a stored hash.</summary>
    /// <param name="password">The plaintext offered.</param>
    /// <param name="hash">The stored hash.</param>
    /// <param name="salt">The stored salt.</param>
    /// <param name="iterations">The stored work factor.</param>
    /// <returns><see langword="true"/> when the password matches.</returns>
    /// <remarks>
    /// <b>Returns false rather than throwing for a malformed input.</b> A login is an untrusted caller, and the difference
    /// between "rejected" and "threw" is observable — a password of the wrong length must not be distinguishable from a
    /// password that was simply wrong. The length rules are enforced at <see cref="Create"/>, where the caller is the
    /// account's owner and a refusal is useful.
    /// </remarks>
    public static bool Verify(string password, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || hash.Length != HashBytes || salt.Length != SaltBytes || iterations <= 0)
        {
            return false;
        }

        if (password.Length > MaximumPasswordLength)
        {
            // Bounded before the KDF runs, so an oversized password cannot be used to make verification expensive.
            return false;
        }

        Span<byte> candidate = stackalloc byte[HashBytes];
        Derive(password, salt, iterations, candidate);

        // Constant-time: a byte-by-byte comparison that returns early leaks how much of the hash matched, which is enough
        // to find the rest one byte at a time.
        return CryptographicOperations.FixedTimeEquals(candidate, hash);
    }

    /// <summary>Whether a password would be accepted by <see cref="Create"/>, without deriving anything.</summary>
    /// <param name="password">The candidate.</param>
    /// <returns><see langword="true"/> when the length is in range.</returns>
    public static bool IsAcceptable(string password)
        => password != null && password.Length >= MinimumPasswordLength && password.Length <= MaximumPasswordLength;

    /// <summary>The one place the KDF is configured, so a hash and its verification cannot disagree.</summary>
    /// <param name="password">The plaintext.</param>
    /// <param name="salt">The salt.</param>
    /// <param name="iterations">The work factor.</param>
    /// <param name="destination">Receives the derived key.</param>
    private static void Derive(string password, ReadOnlySpan<byte> salt, int iterations, Span<byte> destination)
    {
        // UTF-8, named rather than left to the platform: a password with an accent in it must hash the same on the dev box
        // and on the Linux server, and Encoding.Default has been a different thing on each in living memory.
        var bytes = Encoding.UTF8.GetByteCount(password) <= 512 ? stackalloc byte[512] : new byte[Encoding.UTF8.GetByteCount(password)];
        var written = Encoding.UTF8.GetBytes(password, bytes);

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(bytes[..written], salt, destination, iterations, HashAlgorithmName.SHA256);
        }
        finally
        {
            // The plaintext's bytes do not outlive the derivation. The string itself is the CLR's to manage and cannot be
            // cleared, which is why nothing here keeps one.
            CryptographicOperations.ZeroMemory(bytes[..written]);
        }
    }

    /// <summary>Refuses a password the rules do not accept, naming the rule rather than restating it.</summary>
    /// <param name="password">The candidate.</param>
    /// <exception cref="ArgumentException">The password is null, too short or too long.</exception>
    private static void Validate(string password)
    {
        if (!IsAcceptable(password))
        {
            throw new ArgumentException(
                $"a password must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters", nameof(password));
        }
    }
}
