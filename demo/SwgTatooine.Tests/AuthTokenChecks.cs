using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SwgTatooine.Accounts;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// A2: the bearer token's format, its signature, and its expiry.
/// </summary>
/// <remarks>
/// The token is the only thing standing between a WebSocket and somebody else's account, and it is 50 lines of hand-written
/// framing rather than a library — so the cases below are the ones that would be a vulnerability rather than a bug: a forged
/// MAC accepted, an expired token accepted, a truncated token throwing instead of refusing.
/// </remarks>
[TestFixture]
internal sealed class AuthTokenChecks
{
    private static readonly EntityId SomeAccount = EntityId.FromRawValue(0xABCDEF);

    /// <summary>A minted token names the account it was minted for.</summary>
    [Test]
    public void AMintedTokenRoundTrips()
    {
        var token = AuthToken.Mint(SomeAccount);

        Assert.Multiple(() =>
        {
            Assert.That(AuthToken.TryValidate(token, out var account), Is.True);
            Assert.That(account, Is.EqualTo(SomeAccount));
            Assert.That(token, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="),
                "base64URL: a token travels in headers and query strings, where the standard alphabet needs escaping");
        });
    }

    /// <summary>Two tokens for the same account differ, because the expiry is in the signed payload.</summary>
    /// <remarks>
    /// Worth pinning: if the payload carried only the account id, a token would be a permanent credential and the expiry would be
    /// something the server remembered rather than something the token said.
    /// </remarks>
    [Test]
    public void TheExpiryIsPartOfWhatIsSigned()
    {
        var early = AuthToken.Mint(SomeAccount, TimeSpan.FromHours(1));
        var late = AuthToken.Mint(SomeAccount, TimeSpan.FromHours(2));

        Assert.That(early, Is.Not.EqualTo(late), "two lifetimes must not produce one token");
        Assert.That(AuthToken.TryValidate(early, out _), Is.True);
        Assert.That(AuthToken.TryValidate(late, out _), Is.True);
    }

    /// <summary>Every single-character change to a token is refused.</summary>
    /// <remarks>
    /// <b>Every position, not a sampled few.</b> A framing mistake that left one byte out of the MAC's input — the version byte is
    /// the obvious candidate — would be a hole exactly one position wide, and sampling is how that survives a test suite.
    /// </remarks>
    [Test]
    public void EveryTamperedPositionIsRefused()
    {
        var token = AuthToken.Mint(SomeAccount);
        var accepted = new List<int>();

        for (var i = 0; i < token.Length; i++)
        {
            // 'A' and 'B' are both in the base64url alphabet, so the tampered token stays decodable and the MAC is what has to refuse it.
            var swapped = token[i] == 'A' ? 'B' : 'A';
            var tampered = string.Concat(token.AsSpan(0, i), swapped.ToString(), token.AsSpan(i + 1));
            if (tampered != token && AuthToken.TryValidate(tampered, out _))
            {
                accepted.Add(i);
            }
        }

        Assert.That(accepted, Is.Empty, "a token accepted with a byte changed is a forgeable token");
    }

    /// <summary>A token that is not a token is refused rather than throwing.</summary>
    /// <remarks>
    /// <b>This is a regression test for a real throw.</b> <c>Base64Url.TryDecodeFromChars</c> returns false only when the
    /// DESTINATION is too small; an invalid character makes it throw <c>FormatException</c>, which the <c>Try</c> prefix does not
    /// suggest. <c>TryValidate</c> runs inside the admission hook on the transport thread, where an exception is not an error page.
    /// </remarks>
    [Test]
    public void RubbishIsRefusedAndNeverThrows()
    {
        var cases = new[]
        {
            null,
            string.Empty,
            " ",
            "not-a-token",
            "!!!!",
            "====",
            "a",
            new string('A', 64),
            new string('A', 200),
            new string('é', 40),
            AuthToken.Mint(SomeAccount) + "A",
            AuthToken.Mint(SomeAccount)[..^4],
        };

        Assert.Multiple(() =>
        {
            foreach (var bad in cases)
            {
                Assert.That(() => AuthToken.TryValidate(bad, out _), Throws.Nothing, $"validating {bad ?? "null"} threw");
                Assert.That(AuthToken.TryValidate(bad, out var account), Is.False, $"{bad ?? "null"} was accepted");
                Assert.That(account, Is.EqualTo(default(EntityId)));
            }
        });
    }

    /// <summary>An expired token is refused; a token expiring in a moment is not.</summary>
    [Test]
    public void ExpiryIsEnforced()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuthToken.TryValidate(AuthToken.Mint(SomeAccount, TimeSpan.FromSeconds(-1)), out _), Is.False, "one second past is past");
            Assert.That(AuthToken.TryValidate(AuthToken.Mint(SomeAccount, TimeSpan.FromDays(-7)), out _), Is.False);
            Assert.That(AuthToken.TryValidate(AuthToken.Mint(SomeAccount, TimeSpan.FromMinutes(1)), out _), Is.True);
        });
    }

    /// <summary>A zero account never validates, so a zeroed payload cannot be a credential.</summary>
    /// <remarks>
    /// The default <see cref="EntityId"/> is what an uninitialised field holds, and it must not be a valid bearer. Minting one is
    /// the only way to make the token; refusing it on the way back out is what makes that harmless.
    /// </remarks>
    [Test]
    public void TheZeroAccountIsNeverValid()
        => Assert.That(AuthToken.TryValidate(AuthToken.Mint(default), out _), Is.False);

    /// <summary>Validation is thread-safe, because the admission hook runs on whatever transport thread decoded the HELLO.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void ValidationIsConcurrentSafe()
    {
        var tokens = new string[16];
        var accounts = new EntityId[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            accounts[i] = EntityId.FromRawValue((ulong)(i + 1));
            tokens[i] = AuthToken.Mint(accounts[i]);
        }

        var failures = 0;
        Parallel.For(0, 2_000, i =>
        {
            var slot = i % tokens.Length;
            if (!AuthToken.TryValidate(tokens[slot], out var got) || got != accounts[slot])
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.That(failures, Is.Zero);
    }
}

/// <summary>
/// A2: the <c>/auth</c> rate limiter.
/// </summary>
/// <remarks>
/// Every case uses an explicit, fast refill rather than the production six seconds: the behaviour under test is the bucket, and
/// waiting for a real refill would make this fixture the slowest in the suite for no extra coverage.
/// </remarks>
[TestFixture]
internal sealed class AuthRateLimiterChecks
{
    /// <summary>The burst is the capacity, and the attempt after it is refused.</summary>
    [Test]
    public void TheBurstIsTheCapacityAndThenItRefuses()
    {
        var limiter = new AuthRateLimiter(3, TimeSpan.FromHours(1), 100);

        Assert.Multiple(() =>
        {
            Assert.That(limiter.TryTake("ip:1"), Is.True, "first");
            Assert.That(limiter.TryTake("ip:1"), Is.True, "second");
            Assert.That(limiter.TryTake("ip:1"), Is.True, "third");
            Assert.That(limiter.TryTake("ip:1"), Is.False, "the fourth is past a capacity of three");
        });
    }

    /// <summary>Keys are independent, so one exhausted key does not refuse another.</summary>
    [Test]
    public void KeysAreIndependent()
    {
        var limiter = new AuthRateLimiter(1, TimeSpan.FromHours(1), 100);

        Assert.Multiple(() =>
        {
            Assert.That(limiter.TryTake("ip:1"), Is.True);
            Assert.That(limiter.TryTake("ip:1"), Is.False);
            Assert.That(limiter.TryTake("ip:2"), Is.True, "a different address has its own allowance");
        });
    }

    /// <summary>
    /// An attempt refused by one of its keys charges NEITHER.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that matters, and getting it wrong is a denial of service rather than a leak.</b> If the address were
    /// charged for an attempt the name's bucket refused, one exhausted name could burn the whole allowance of every person behind
    /// that address — an attacker with one account name could lock out an office.
    /// </remarks>
    [Test]
    public void AnAttemptRefusedByOneKeyChargesNeither()
    {
        var limiter = new AuthRateLimiter(2, TimeSpan.FromHours(1), 100);

        // Exhaust the name alone.
        Assert.That(limiter.TryTake("name:leia"), Is.True);
        Assert.That(limiter.TryTake("name:leia"), Is.True);

        // Now a paired attempt: the name is empty, so this must be refused — and must not have spent the address's tokens.
        Assert.That(limiter.TryTake("ip:1", "name:leia"), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(limiter.TryTake("ip:1", "name:han"), Is.True, "the address kept its first token");
            Assert.That(limiter.TryTake("ip:1", "name:chewie"), Is.True, "and its second");
            Assert.That(limiter.TryTake("ip:1", "name:lando"), Is.False, "two is the capacity, so the third is refused on the address");
        });
    }

    /// <summary>Time refills the bucket, at the rate it was configured with.</summary>
    [Test]
    public void TimeRefillsTheBucket()
    {
        var limiter = new AuthRateLimiter(1, TimeSpan.FromMilliseconds(30), 100);
        Assert.That(limiter.TryTake("ip:1"), Is.True);
        Assert.That(limiter.TryTake("ip:1"), Is.False);

        // A spin rather than a sleep: Thread.Sleep's resolution is 15.6 ms on Windows, which is half the interval being measured.
        var spin = Stopwatch.StartNew();
        while (spin.Elapsed < TimeSpan.FromMilliseconds(60))
        {
            Thread.SpinWait(2_000);
        }

        Assert.That(limiter.TryTake("ip:1"), Is.True, "60 ms must have refilled a 30 ms token");
    }

    /// <summary>A null key is ignored rather than tracked, which is what an unknown client address produces.</summary>
    [Test]
    public void ANullKeyIsIgnored()
    {
        var limiter = new AuthRateLimiter(1, TimeSpan.FromHours(1), 100);

        Assert.That(limiter.TryTake(null, "name:leia"), Is.True);
        Assert.That(limiter.TrackedKeys, Is.EqualTo(1), "only the name was tracked");
        Assert.That(limiter.TryTake(null, "name:leia"), Is.False, "and the name's limit still applies");
    }

    /// <summary>
    /// The table is bounded, so a client choosing the keys cannot exhaust memory.
    /// </summary>
    /// <remarks>
    /// An account name is attacker-chosen, so an unbounded table keyed on one is a memory-exhaustion vector. Eviction only ever
    /// forgives an attempt — a fresh bucket starts full — so it cannot be used to grant one either.
    /// </remarks>
    [Test]
    public void TheTableIsBounded()
    {
        var limiter = new AuthRateLimiter(1, TimeSpan.FromHours(1), 64);

        for (var i = 0; i < 1_000; i++)
        {
            limiter.TryTake($"name:{i}");
        }

        Assert.That(limiter.TrackedKeys, Is.LessThanOrEqualTo(64), "the table grew past its bound");
    }

    /// <summary>Concurrent attempts never hand out more than the capacity.</summary>
    /// <remarks>
    /// The bucket is read and charged in two passes, so a lost update here would mean the burst is larger than configured under
    /// load — which is exactly when a rate limit is supposed to work.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ConcurrentAttemptsRespectTheCapacity()
    {
        const int capacity = 10;
        var limiter = new AuthRateLimiter(capacity, TimeSpan.FromHours(1), 100);
        var granted = 0;

        Parallel.For(0, 500, _ =>
        {
            if (limiter.TryTake("ip:1"))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.That(granted, Is.EqualTo(capacity), "a one-hour refill means exactly the capacity may be granted");
    }
}
