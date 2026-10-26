using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// A1 — password hashing, and the two declarations on <see cref="Account"/> that the rest of identity depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authentication is the one place in this demo where "it is only a demo" is a bad argument</b>, because it is the one
/// surface a visitor judges on its defaults. These checks pin the properties that are invisible when they are wrong: that a
/// salt is per-account, that the stored work factor is what verification uses, that a comparison does not leak, and that the
/// plaintext is bounded before the KDF runs.
/// </para>
/// <para>
/// Fast by construction except where the KDF is genuinely exercised — <see cref="PasswordHash.DefaultIterations"/> is 600 000
/// rounds and costs a few hundred microseconds, so the round-trip cases use it sparingly and the rest pass a small factor.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
public sealed class AccountCredentialChecks
{
    /// <summary>A password verifies against its own hash, and nothing else does.</summary>
    [Test]
    public void APasswordVerifiesAgainstItsOwnHashOnly()
    {
        var hash = new byte[PasswordHash.HashBytes];
        var salt = new byte[PasswordHash.SaltBytes];
        PasswordHash.Create("correct horse battery", hash, salt, out var iterations);

        Assert.Multiple(() =>
        {
            Assert.That(PasswordHash.Verify("correct horse battery", hash, salt, iterations), Is.True);
            Assert.That(PasswordHash.Verify("correct horse batterx", hash, salt, iterations), Is.False, "one character out is out");
            Assert.That(PasswordHash.Verify("correct horse battery ", hash, salt, iterations), Is.False, "trailing space is part of the password");
            Assert.That(PasswordHash.Verify(string.Empty, hash, salt, iterations), Is.False);
            Assert.That(PasswordHash.Verify(null, hash, salt, iterations), Is.False, "a login is an untrusted caller and must not throw");
        });
    }

    /// <summary>
    /// Two accounts with the same password get different salts, and therefore different hashes.
    /// </summary>
    /// <remarks>
    /// The property a salt exists for: without it, one precomputed table breaks every account at once, and two identical
    /// hashes in the database would tell an attacker that two people chose the same password.
    /// </remarks>
    [Test]
    public void TheSamePasswordHashesDifferentlyForTwoAccounts()
    {
        var hashA = new byte[PasswordHash.HashBytes];
        var saltA = new byte[PasswordHash.SaltBytes];
        var hashB = new byte[PasswordHash.HashBytes];
        var saltB = new byte[PasswordHash.SaltBytes];

        PasswordHash.Create("the same password", hashA, saltA, out _);
        PasswordHash.Create("the same password", hashB, saltB, out _);

        Assert.Multiple(() =>
        {
            Assert.That(saltA.AsSpan().SequenceEqual(saltB), Is.False, "a salt is per account");
            Assert.That(hashA.AsSpan().SequenceEqual(hashB), Is.False, "so the hashes differ even for one password");
        });
    }

    /// <summary>
    /// A hash verifies at the work factor it was made with, which is what lets the cost be raised later.
    /// </summary>
    /// <remarks>
    /// <b>The reason <see cref="Account.Iterations"/> is stored per account rather than read from the constant.</b> If
    /// verification used today's <see cref="PasswordHash.DefaultIterations"/>, then raising it would lock out every account
    /// created before the change — so the ability to raise it at all depends on this test's claim holding.
    /// </remarks>
    [Test]
    public void AHashVerifiesAtTheFactorItWasMadeWith()
    {
        var hash = new byte[PasswordHash.HashBytes];
        var salt = new byte[PasswordHash.SaltBytes];
        PasswordHash.Create("a password worth keeping", hash, salt, out var iterations);

        Assert.Multiple(() =>
        {
            Assert.That(iterations, Is.EqualTo(PasswordHash.DefaultIterations), "a new hash uses the current factor");
            Assert.That(PasswordHash.Verify("a password worth keeping", hash, salt, iterations), Is.True);
            Assert.That(PasswordHash.Verify("a password worth keeping", hash, salt, iterations + 1), Is.False,
                "and a different factor is a different hash — which is why the stored one has to be used");
        });
    }

    /// <summary>The length rules refuse at creation and merely reject at verification.</summary>
    /// <remarks>
    /// The asymmetry is deliberate. At creation the caller owns the account and a refusal naming the rule is useful; at
    /// verification the caller is untrusted, and a password of the wrong length must be indistinguishable from one that was
    /// simply wrong — a throw would be an oracle.
    /// </remarks>
    [Test]
    public void LengthIsRefusedOnCreationAndRejectedOnVerification()
    {
        var hash = new byte[PasswordHash.HashBytes];
        var salt = new byte[PasswordHash.SaltBytes];

        Assert.Multiple(() =>
        {
            Assert.That(PasswordHash.IsAcceptable(new string('x', PasswordHash.MinimumPasswordLength)), Is.True);
            Assert.That(PasswordHash.IsAcceptable(new string('x', PasswordHash.MinimumPasswordLength - 1)), Is.False);
            Assert.That(PasswordHash.IsAcceptable(new string('x', PasswordHash.MaximumPasswordLength)), Is.True);
            Assert.That(PasswordHash.IsAcceptable(new string('x', PasswordHash.MaximumPasswordLength + 1)), Is.False);
            Assert.That(PasswordHash.IsAcceptable(null), Is.False);
        });

        Assert.That(() => PasswordHash.Create("short", hash, salt, out _), Throws.ArgumentException, "too short is refused by name");

        PasswordHash.Create("a long enough password", hash, salt, out var iterations);
        Assert.That(PasswordHash.Verify(new string('x', PasswordHash.MaximumPasswordLength + 1), hash, salt, iterations), Is.False,
            "an oversized password is rejected without running the KDF, so it cannot be used to make verification expensive");
    }

    /// <summary>A non-ASCII password hashes the same wherever it runs.</summary>
    /// <remarks>
    /// UTF-8 is named in the implementation rather than left to the platform, because <c>Encoding.Default</c> has been a
    /// different thing on Windows and Linux in living memory — and this demo hashes on a dev box and verifies on a Linux
    /// server. A silent divergence there would lock every affected account out on deployment and nowhere else.
    /// </remarks>
    [Test]
    public void ANonAsciiPasswordRoundTrips()
    {
        var hash = new byte[PasswordHash.HashBytes];
        var salt = new byte[PasswordHash.SaltBytes];
        const string password = "mot de passe très sûr 😀";

        PasswordHash.Create(password, hash, salt, out var iterations);

        Assert.Multiple(() =>
        {
            Assert.That(PasswordHash.Verify(password, hash, salt, iterations), Is.True);
            Assert.That(PasswordHash.Verify("mot de passe tres sur 😀", hash, salt, iterations), Is.False, "the accents are part of it");
        });
    }

    /// <summary>A malformed stored hash is rejected rather than throwing.</summary>
    /// <remarks>
    /// Reached if a record is ever truncated or a field reordered. A login path that threw on it would turn a data problem
    /// into a 500 on an endpoint an unauthenticated caller can reach.
    /// </remarks>
    [Test]
    public void AMalformedStoredHashIsRejected()
    {
        var salt = new byte[PasswordHash.SaltBytes];
        Assert.Multiple(() =>
        {
            Assert.That(PasswordHash.Verify("a password", new byte[PasswordHash.HashBytes - 1], salt, 1000), Is.False);
            Assert.That(PasswordHash.Verify("a password", new byte[PasswordHash.HashBytes], new byte[1], 1000), Is.False);
            Assert.That(PasswordHash.Verify("a password", new byte[PasswordHash.HashBytes], salt, 0), Is.False, "no work factor is not a hash");
            Assert.That(PasswordHash.Verify("a password", new byte[PasswordHash.HashBytes], salt, -1), Is.False);
        });
    }

    /// <summary>
    /// <see cref="Account"/> is Versioned and its name is uniquely indexed — the two declarations the rest of identity rests on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted from the attributes rather than from behaviour, deliberately.</b> Proving the index by inserting a duplicate
    /// needs a real database and belongs with the signup path that does the inserting (A2). What this catches is cheaper and
    /// is the thing most likely to happen: somebody removing <c>[Index]</c> or changing the storage mode while refactoring,
    /// after which signup would silently stop being race-free and an account would survive only to the next checkpoint.
    /// </para>
    /// <para>
    /// The name is the demo's first index anywhere, so there is no neighbouring declaration to notice its absence.
    /// </para>
    /// </remarks>
    [Test]
    public void AccountIsVersionedAndItsNameIsUniquelyIndexed()
    {
        var component = typeof(Account).GetCustomAttribute<ComponentAttribute>();
        var name = typeof(Account).GetField(nameof(Account.Name));
        var index = name?.GetCustomAttribute<IndexAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(component, Is.Not.Null, "Account is a component");
            Assert.That(component.StorageMode, Is.EqualTo(StorageMode.Versioned),
                "an account is the one thing a person created and expects to find again, so it rides the WAL");
            Assert.That(name, Is.Not.Null);
            Assert.That(name.FieldType, Is.EqualTo(typeof(String64)), "a blittable inline string, never a heap reference");
            Assert.That(index, Is.Not.Null, "the name is indexed — this is the demo's first index anywhere");
            Assert.That(index.AllowMultiple, Is.False, "and it is UNIQUE: that is what makes 'that name is taken' a refusal and not a race");
        });
    }

    /// <summary>The digest and salt spans are exactly the sizes the KDF writes, and they land inside the component.</summary>
    /// <remarks>
    /// <para>
    /// A component is a fixed-size block, so these cannot be arrays, and the schema layer refuses an <c>[InlineArray]</c>
    /// outright — so they are <see cref="ulong"/> words with byte-span accessors over them. A size that drifted from
    /// <see cref="PasswordHash.HashBytes"/> would silently truncate or over-read a hash.
    /// </para>
    /// <para>
    /// <b>The span must alias the component, not a copy.</b> That is the whole point of <c>[UnscopedRef]</c> here: if the
    /// accessor returned a span over a temporary, <c>PasswordHash.Create</c> would write a hash that is discarded and every
    /// login would fail against a zero digest. The write-through assertion below is what catches that.
    /// </para>
    /// </remarks>
    [Test]
    public void TheCredentialBuffersMatchTheKdf()
    {
        var secret = default(AccountSecret);

        // Lengths read out before the lambda: a Span is a ref struct and cannot be captured, which is the compiler enforcing that these alias the component.
        var digestLength = secret.DigestSpan().Length;
        var saltLength = secret.SaltSpan().Length;

        Assert.Multiple(() =>
        {
            Assert.That(digestLength, Is.EqualTo(PasswordHash.HashBytes));
            Assert.That(saltLength, Is.EqualTo(PasswordHash.SaltBytes));
            Assert.That(AccountSecret.DigestWords * sizeof(ulong), Is.EqualTo(PasswordHash.HashBytes));
            Assert.That(AccountSecret.SaltWords * sizeof(ulong), Is.EqualTo(PasswordHash.SaltBytes));
        });

        // Write through the span and read back through the fields: this is the aliasing claim, and nothing else in the suite makes it.
        secret.DigestSpan()[0] = 0xAB;
        secret.DigestSpan()[PasswordHash.HashBytes - 1] = 0xCD;
        secret.SaltSpan()[0] = 0xEF;

        Assert.Multiple(() =>
        {
            Assert.That(secret.Digest0 & 0xFF, Is.EqualTo(0xABUL), "the first digest byte must be the first word's low byte");
            Assert.That(secret.Digest3 >> 56, Is.EqualTo(0xCDUL), "the last digest byte must be the last word's high byte");
            Assert.That(secret.Salt0 & 0xFF, Is.EqualTo(0xEFUL));
        });
    }

    /// <summary>
    /// Both components fit the inline payload a <c>Spawn</c> carries, which is why they are two components and not one.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test for a silent process death, not a style check.</b> One combined component came to 132 bytes;
    /// <c>ComponentValue</c> carries 112 inline and wrote the excess past its <c>fixed</c> buffer, because the size check was
    /// gated on <c>CheckConfig.Enabled</c> — false by default, with no runtime setter. The spawn killed the host with no
    /// exception, no message and no stack. The engine's check is ungated now; this asserts the demo stays on the right side of
    /// it, since a field added to either component is the way back over the line.
    /// </remarks>
    [Test]
    public void BothAccountComponentsFitAnInlineSpawnPayload()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<Account>(), Is.LessThanOrEqualTo(ComponentValue.MaxPayloadSize),
                "Account must be spawnable inline — split it before growing it past the limit");
            Assert.That(Marshal.SizeOf<AccountSecret>(), Is.LessThanOrEqualTo(ComponentValue.MaxPayloadSize));
        });
    }

    /// <summary>A hash written into an <see cref="AccountSecret"/> verifies when read back out of it.</summary>
    /// <remarks>
    /// The round trip through the component's own storage, which is where a size or layout mistake would show up as a login
    /// that never succeeds.
    /// </remarks>
    [Test]
    public void AnAccountStoresAVerifiableCredential()
    {
        var account = default(Account);
        account.Name = "nockawa";
        account.CreatedUtcTicks = DateTime.UtcNow.Ticks;

        var secret = default(AccountSecret);
        PasswordHash.Create("a password worth keeping", secret.DigestSpan(), secret.SaltSpan(), out var iterations);
        secret.Iterations = iterations;

        Assert.Multiple(() =>
        {
            Assert.That(account.Name.AsString, Is.EqualTo("nockawa"));
            Assert.That(PasswordHash.Verify("a password worth keeping", secret.DigestSpan(), secret.SaltSpan(), secret.Iterations), Is.True);
            Assert.That(PasswordHash.Verify("the wrong one entirely", secret.DigestSpan(), secret.SaltSpan(), secret.Iterations), Is.False);
            Assert.That(account.LastSeenUtcTicks, Is.Zero, "never logged in yet");
        });
    }
}
