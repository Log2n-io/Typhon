using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Accounts;
using SwgTatooine.Bots;

namespace SwgTatooine.Tests;

/// <summary>
/// B5: a bot population derives its credentials, signs itself up through the real <c>/auth</c>, and keeps nothing it should not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cross-assembly checks are the point of this fixture.</b> A derived password and a generated name are only correct
/// relative to rules that live in the SERVER — <see cref="PasswordHash"/>'s 8..256 and <see cref="AccountName"/>'s
/// "3 to 24, starts with a letter, letters digits and underscore". Asserting them against a copy of those numbers would pass while
/// the fleet was refused by every signup; asserting them against the server's own predicates is what makes the test worth having.
/// That is also why <c>SwgTatooine.Tests</c> now references the bots project.
/// </para>
/// <para>
/// <b>And the negative one matters more than any of them.</b> The whole design of the accounts file is that a thousand live
/// credentials never reach the disk, so there is a test that reads the written bytes back and fails if a derived password appears
/// in them. That invariant is invisible to a type system and would survive any amount of review.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class BotAccountChecks
{
    private const string Key = "a-fleet-key-long-enough";

    /// <summary>A derivation is a function: the same key and index give the same password, every time and in any process.</summary>
    [Test]
    public void ADerivedPasswordIsStableAndIndexSpecific()
    {
        var first = BotCredentials.Password(Key, 7);

        Assert.Multiple(() =>
        {
            Assert.That(
                BotCredentials.Password(Key, 7),
                Is.EqualTo(first),
                "the same key and index must derive the same password, or a second run cannot log in");
            Assert.That(BotCredentials.Password(Key, 8), Is.Not.EqualTo(first), "two bots must not share a password");
            Assert.That(BotCredentials.Password(Key + "x", 7), Is.Not.EqualTo(first), "a different key must derive a different password");
            Assert.That(first, Has.Length.EqualTo(BotCredentials.PasswordLength));
        });
    }

    /// <summary>
    /// Every derived credential is acceptable to the server's OWN rules, at the ends of the range and not only in the middle.
    /// </summary>
    /// <param name="index">The bot index under test.</param>
    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(999)]
    [TestCase(99_999)]
    [TestCase(999_999)]
    public void ADerivedCredentialSatisfiesTheServersRules(int index)
    {
        var name = BotCredentials.Name(index);
        var password = BotCredentials.Password(Key, index);

        Assert.Multiple(() =>
        {
            // AccountName's predicate, not a transcription of it: if the rule tightens, this fails instead of the fleet.
            Assert.That(AccountName.IsAcceptable(name), Is.True, $"the server would refuse the name '{name}': {AccountName.Rule}");
            Assert.That(PasswordHash.IsAcceptable(password), Is.True, "the server would refuse the derived password's length");
            Assert.That(name, Does.StartWith("bot"));
        });
    }

    /// <summary>A name that is one character short of the limit is still a name — the padding is what guarantees it.</summary>
    [Test]
    public void ANameIsWellInsideTheLengthLimit()
    {
        Assert.That(BotCredentials.Name(999_999), Has.Length.LessThanOrEqualTo(AccountName.MaximumLength));
        Assert.That(BotCredentials.Name(0), Has.Length.GreaterThanOrEqualTo(AccountName.MinimumLength));
    }

    /// <summary>
    /// A missing or token key is refused at startup, with a message naming the variable.
    /// </summary>
    /// <remarks>
    /// The message is asserted, not just the throw: the failure mode this guards against is an operator who sets nothing, runs the
    /// fleet, and has to work out from a stack trace why a thousand accounts cannot be reproduced.
    /// </remarks>
    [Test]
    public void AMissingOrTooShortKeyIsRefusedByName()
    {
        var saved = Environment.GetEnvironmentVariable(BotCredentials.KeyVariable);
        try
        {
            Environment.SetEnvironmentVariable(BotCredentials.KeyVariable, null);
            var missing = Assert.Throws<InvalidOperationException>(() => BotCredentials.ReadKeyOrThrow());

            Environment.SetEnvironmentVariable(BotCredentials.KeyVariable, "short");
            var tooShort = Assert.Throws<InvalidOperationException>(() => BotCredentials.ReadKeyOrThrow());

            Assert.Multiple(() =>
            {
                Assert.That(missing.Message, Does.Contain(BotCredentials.KeyVariable));
                Assert.That(tooShort.Message, Does.Contain(BotCredentials.KeyVariable));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotCredentials.KeyVariable, saved);
        }
    }

    /// <summary>
    /// The written file declares no password field, carries no byte-order mark, and leaves no temporary file behind.
    /// </summary>
    /// <remarks>
    /// <b>This does NOT prove that no password reaches the disk, and it used to claim it did.</b>
    /// <see cref="BotAccountStore"/> never sees a password — it has no access to <c>SWG_BOT_KEY</c> and nothing here hands it one
    /// — so a loop asserting that a derived password is absent from the file was asserting that a value the code under test has
    /// never been given is not present. It could not fail. The real claim is tested where a password genuinely transits, which is
    /// <see cref="BotProvisionerChecks.NoPasswordReachesTheAccountsFile"/>, after an actual signup.
    /// <para>
    /// What is left here is worth keeping and is honest: the file's shape. Read back as bytes rather than inspected field by
    /// field, because a future field or a serializer setting could add something unwanted without any property being called it.
    /// </para>
    /// </remarks>
    [Test]
    public void TheWrittenFileHasTheShapeOtherToolsExpect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new BotAccountStore(path, 4);
            for (var i = 0; i < 4; i++)
            {
                store.Put(new BotAccountRecord
                {
                    Index = i,
                    Name = BotCredentials.Name(i),
                    SignedUp = true,
                    Token = "token-" + i,
                    TokenExpUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200,
                });
            }

            store.Flush();
            var written = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(written, Does.Not.Contain("password"), "the file declares a password field");
                Assert.That(written, Does.Contain(BotCredentials.Name(0)), "the file must still name the accounts");
                Assert.That(File.Exists(path + ".tmp"), Is.False, "the atomic write left its temporary file behind");

                // No BOM. The file is JSONL so that other tools can read it a line at a time, and a leading U+FEFF makes the
                // first line fail in every one of them.
                Assert.That(File.ReadAllBytes(path)[0], Is.EqualTo((byte)'{'), "the accounts file starts with a byte-order mark");
            });
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>Every field survives a round trip through the file, including the resume token as bytes.</summary>
    [Test]
    public void ARecordRoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            var resume = new byte[] { 1, 2, 3, 250, 251, 252, 0, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
            var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200;

            var writing = new BotAccountStore(path, 3);
            writing.Put(new BotAccountRecord { Index = 1, Name = "bot000001", SignedUp = true, Token = "tok", TokenExpUnix = expiry });
            writing.PutResume(1, resume);
            writing.Flush();

            var reading = new BotAccountStore(path, 3);
            var record = reading[1];

            Assert.Multiple(() =>
            {
                Assert.That(record, Is.Not.Null);
                Assert.That(record.Name, Is.EqualTo("bot000001"));
                Assert.That(record.SignedUp, Is.True);
                Assert.That(record.Token, Is.EqualTo("tok"));
                Assert.That(record.TokenExpUnix, Is.EqualTo(expiry));
                Assert.That(reading.Resume(1), Is.EqualTo(resume), "the resume token must come back byte for byte or a restart cold-logs");
                Assert.That(reading[0], Is.Null, "a bot that was never provisioned must read as absent, not as a blank record");
            });
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>A line that cannot be parsed costs one bot and is counted, not thrown.</summary>
    /// <remarks>
    /// A truncated last line is the ordinary result of killing the fleet mid-flush. Failing the whole run over it would make every
    /// kill cost a full re-provisioning; absorbing it silently would hide a file that is losing records.
    /// </remarks>
    [Test]
    public void ACorruptLineIsSkippedAndCounted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "{\"i\":0,\"name\":\"bot000000\",\"signedUp\":true}\n{\"i\":1,\"name\":\"bot0\n");
            var store = new BotAccountStore(path, 4);

            Assert.Multiple(() =>
            {
                Assert.That(store[0], Is.Not.Null, "the intact line must survive its neighbour");
                Assert.That(store.SkippedLines, Is.EqualTo(1));
            });
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>Writes are batched: below the threshold nothing is written, and crossing it writes once.</summary>
    [Test]
    public void FlushesAreBatchedAtTheThreshold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new BotAccountStore(path, 8, flushThreshold: 4);
            for (var i = 0; i < 3; i++)
            {
                store.Put(new BotAccountRecord { Index = i, Name = BotCredentials.Name(i) });
            }

            Assert.That(store.FlushIfDue(), Is.False, "three changes must not trigger a write at a threshold of four");

            store.Put(new BotAccountRecord { Index = 3, Name = BotCredentials.Name(3) });

            Assert.Multiple(() =>
            {
                Assert.That(store.FlushIfDue(), Is.True);
                Assert.That(store.Pending, Is.Zero, "a flush must clear what it wrote or the next one fires immediately");
                Assert.That(store.Flushes, Is.EqualTo(1));
            });
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// A <c>WELCOME</c> that carried no resume token must not erase the one on disk.
    /// </summary>
    /// <remarks>
    /// <b>The bug this pins down was silent and total.</b> <c>TyphonClient</c> assigns its resume token from every <c>WELCOME</c>
    /// unconditionally, so a server running with <c>--resume-grace 0</c> hands back none and the client's token becomes null. The
    /// swarm forwarded that null straight into the store, which wrote it — so pointing a fleet at a server with resume disabled
    /// erased every stored token, and the next restart was a thousand cold logins with nothing anywhere reporting why.
    /// </remarks>
    [Test]
    public void AnAbsentResumeTokenLeavesTheStoredOneAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            var resume = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6 };
            var store = new BotAccountStore(path, 2);
            store.Put(new BotAccountRecord { Index = 0, Name = "bot000000", SignedUp = true, Token = "tok" });
            store.PutResume(0, resume);

            store.PutResume(0, null);
            store.PutResume(0, []);

            Assert.Multiple(() =>
            {
                Assert.That(store.Resume(0), Is.EqualTo(resume), "a WELCOME with no token must not be written through as a forgotten one");

                // And clearing is still possible — it just has to be asked for by name.
                store.ForgetResume(0);
                Assert.That(store.Resume(0), Is.Null, "ForgetResume is the explicit way to drop continuity and must still work");
            });
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Every line that could not be used is counted, including the two shapes that used to vanish.
    /// </summary>
    /// <remarks>
    /// A negative index used to fall into the same branch as an index past the population — which is legitimate, since a
    /// thousand-bot file read by a hundred-bot run is the ordinary case — and so was dropped uncounted. A duplicate index was
    /// also silently overwritten, and that is the one case where the file is PROVABLY losing a record: two lines claim one bot.
    /// A count that is a partial truth is worse than no count, because it reads as a clean file.
    /// </remarks>
    [Test]
    public void EveryUnusableLineIsCountedAndAnOversizedFileIsNot()
    {
        var corrupt = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        var bigger = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(
                corrupt,
                "{\"i\":-3,\"name\":\"botneg\"}\n"
                + "{\"i\":0,\"name\":\"bot000000\",\"signedUp\":true}\n"
                + "{\"i\":0,\"name\":\"bot000000\",\"signedUp\":true,\"token\":\"second\"}\n"
                + "{\"i\":1,\"name\":\"trunc\n");

            var store = new BotAccountStore(corrupt, 4);

            // A file describing a BIGGER fleet than this run wants is not corruption and must not be counted.
            File.WriteAllText(bigger, "{\"i\":0,\"name\":\"bot000000\"}\n{\"i\":9,\"name\":\"bot000009\"}\n");
            var narrowed = new BotAccountStore(bigger, 2);

            Assert.Multiple(() =>
            {
                // the negative index, the duplicate, and the truncated last line
                Assert.That(store.SkippedLines, Is.EqualTo(3), "a negative index, a duplicate and a truncated line are all unusable");
                Assert.That(store[0]?.Token, Is.EqualTo("second"), "the later line still wins; the point is that the loss is reported");
                Assert.That(narrowed.SkippedLines, Is.Zero, "a file describing a bigger fleet is ordinary, not corrupt");
                Assert.That(narrowed[0], Is.Not.Null);
            });
        }
        finally
        {
            Delete(corrupt);
            Delete(bigger);
        }
    }

    /// <summary>A resume token longer than the engine mints today still decodes.</summary>
    /// <remarks>
    /// The decode buffer was a <c>stackalloc byte[32]</c> sized from the sixteen bytes the engine happens to mint.
    /// <c>Convert.TryFromBase64String</c> answers false for a buffer that is too small rather than throwing, so the day the token
    /// grew, every one of them would have turned into a cold login — a decode path failing closed on a value it could have read
    /// perfectly well, and reporting nothing.
    /// </remarks>
    [Test]
    public void ALongerResumeTokenStillDecodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bots-{Guid.NewGuid():N}.jsonl");
        try
        {
            var longer = new byte[64];
            for (var i = 0; i < longer.Length; i++)
            {
                longer[i] = (byte)(i * 7);
            }

            var store = new BotAccountStore(path, 1);
            store.Put(new BotAccountRecord { Index = 0, Name = "bot000000", SignedUp = true });
            store.PutResume(0, longer);

            Assert.That(store.Resume(0), Is.EqualTo(longer), "a token the store cannot size for must not silently read back as absent");
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Lowering the work factor does not invalidate accounts that already exist.
    /// </summary>
    /// <remarks>
    /// <b>This is the property that makes the knob safe, and it is worth a test because the alternative is silent.</b> The
    /// work factor is stored per account and verification uses the STORED one, so a run that lowers it still logs in every
    /// account made before. Had <c>Verify</c> used the current setting instead, lowering it would have locked out every
    /// existing user with a wrong-password error and no explanation.
    /// </remarks>
    [Test]
    public void LoweringTheWorkFactorStillVerifiesOlderAccounts()
    {
        var original = PasswordHash.Iterations;
        try
        {
            // Heap arrays, not stackalloc: a ref local cannot be captured by the Assert.Multiple lambda.
            var hash = new byte[PasswordHash.HashBytes];
            var salt = new byte[PasswordHash.SaltBytes];

            // Hashed at the strong default...
            PasswordHash.Iterations = PasswordHash.DefaultIterations;
            PasswordHash.Create("a-representative-password", hash, salt, out var stored);

            // ...then the run lowers it, as a throwaway demo may.
            PasswordHash.Iterations = 100_000;

            var verified = PasswordHash.Verify("a-representative-password", hash, salt, stored);
            var rejected = PasswordHash.Verify("the-wrong-password", hash, salt, stored);
            PasswordHash.Create("another-password", hash, salt, out var now);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.EqualTo(PasswordHash.DefaultIterations), "the work factor used must be reported for storage");
                Assert.That(verified, Is.True, "an account hashed before the change must still log in");
                Assert.That(rejected, Is.False);
                Assert.That(now, Is.EqualTo(100_000), "a new password must use the current work factor");
            });
        }
        finally
        {
            PasswordHash.Iterations = original;
        }
    }

    /// <summary>A work factor below the floor is refused, so "faster" cannot become "decorative".</summary>
    [Test]
    public void AWorkFactorBelowTheFloorIsRefused()
    {
        var original = PasswordHash.Iterations;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(() => PasswordHash.Iterations = PasswordHash.MinimumIterations - 1, Throws.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(() => PasswordHash.Iterations = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(PasswordHash.Iterations, Is.EqualTo(original), "a refused assignment must not change anything");

                // The floor itself is accepted.
                Assert.That(() => PasswordHash.Iterations = PasswordHash.MinimumIterations, Throws.Nothing);
            });
        }
        finally
        {
            PasswordHash.Iterations = original;
        }
    }

    /// <summary>The game endpoint's HTTP side, which is where <c>/auth</c> lives.</summary>
    /// <param name="endpoint">The game endpoint.</param>
    /// <param name="expected">The expected HTTP base, or <see langword="null"/>.</param>
    [Test]
    [TestCase("ws://127.0.0.1:9100/ws", "http://127.0.0.1:9100/")]
    [TestCase("wss://demo.example.com/ws", "https://demo.example.com/")]
    [TestCase("http://127.0.0.1:9100/", "http://127.0.0.1:9100/")]
    [TestCase("tcp://127.0.0.1:9000", null)]
    public void TheAuthBaseFollowsTheGameEndpoint(string endpoint, string expected)
    {
        var resolved = BotProvisioner.AuthBaseFrom(new Uri(endpoint));
        Assert.That(resolved?.ToString(), Is.EqualTo(expected), "a tcp:// endpoint has no HTTP side and must say so rather than be guessed at");
    }

    /// <summary>Removes a temp file without caring whether it was there.</summary>
    /// <param name="path">The file.</param>
    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
        catch (IOException)
        {
            // A temp file we could not remove is not a test failure.
        }
    }
}

/// <summary>
/// B5 end to end: a population provisions itself against a real <c>/auth</c> host, and the tokens it gets are ones the game's
/// admission hook accepts.
/// </summary>
/// <remarks>
/// <b>A real Kestrel and the real endpoints, for the reason <see cref="AuthEndpointChecks"/> gives</b> — the host's
/// source-generated JSON means a shape mismatch compiles and fails only on the wire. The bots declare their own copy of the
/// request and response records (07-bots § 1 keeps them off the server's assemblies), so the two shapes agreeing is itself
/// something only a round trip can prove.
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class BotProvisionerChecks
{
    private const string Key = "a-fleet-key-long-enough";
    private const int Population = 6;

    private string _dir;
    private string _accountsPath;
    private TatooineSim _sim;
    private WebApplication _app;
    private Uri _authBase;

    [SetUp]
    public async Task SetUp()
    {
        _dir = Worlds.NewDirectory();
        _accountsPath = Path.Combine(_dir, "bots.jsonl");

        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;
        _sim = new TatooineSim(config);
        _sim.Initialize();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthJson();
        _app = builder.Build();

        // A capacity that cannot be the thing under test. The limiter's own behaviour has its own fixture, and the point here is
        // that the limiter is exactly why the server grew --auth-burst: a bot box is one address key.
        _app.MapAuth(_sim.Dbe, new AuthRateLimiter(10_000, TimeSpan.FromSeconds(1), 10_000));

        await _app.StartAsync();
        _authBase = new Uri(BoundAddress(_app));
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _sim?.Dispose();
        _sim = null;
        Worlds.Delete(_dir);
    }

    /// <summary>A fresh population signs itself up, and every token it is handed is one the game would admit.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task AFreshPopulationSignsUpAndGetsAdmissibleTokens()
    {
        var store = new BotAccountStore(_accountsPath, Population);
        ProvisionReport report;
        using (var provisioner = new BotProvisioner(_authBase, store, Key, concurrency: 4))
        {
            report = await provisioner.EnsureAsync(Population, null, CancellationToken.None);
        }

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.Zero, report.FirstFailure);
            Assert.That(report.Created, Is.EqualTo(Population));
            Assert.That(report.Reused, Is.Zero);
            Assert.That(store.UsableTokens(0), Is.EqualTo(Population));
        });

        // The loop closed: the token the HTTP endpoint minted is the one TatooineReplication.Admit validates before it will bind an
        // avatar. Without this the fleet could provision perfectly and still be refused 4102 by every session.
        // Distinct accounts too, not one account presented N times: a player session IS its account, so a fleet sharing one would
        // measure a single avatar being fought over rather than a population of sessions.
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Population; i++)
        {
            Assert.That(AuthToken.TryValidate(store[i].Token, out var account), Is.True, $"bot {i}'s token would be refused on HELLO");
            Assert.That(ids.Add(account.ToString()), Is.True, $"bot {i}'s token names an account an earlier bot already holds");
            Assert.That(names.Add(store[i].Name), Is.True, $"bot {i} shares an account name with an earlier bot");
        }
    }

    /// <summary>A second run of the same fleet makes no HTTP call at all: the stored tokens are still good.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task ASecondPassReusesTheStoredTokens()
    {
        using (var first = new BotProvisioner(_authBase, new BotAccountStore(_accountsPath, Population), Key, concurrency: 4))
        {
            var initial = await first.EnsureAsync(Population, null, CancellationToken.None);
            Assert.That(initial.Failed, Is.Zero, initial.FirstFailure);
        }

        var reopened = new BotAccountStore(_accountsPath, Population);
        using var second = new BotProvisioner(_authBase, reopened, Key, concurrency: 4);
        var report = await second.EnsureAsync(Population, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(report.Reused, Is.EqualTo(Population), "a run whose tokens are still valid must not re-authenticate the fleet");
            Assert.That(report.Created, Is.Zero);
            Assert.That(report.LoggedIn, Is.Zero);
        });
    }

    /// <summary>
    /// A fleet whose accounts file was lost logs in instead of failing: the server still has the accounts, and the passwords are
    /// derivable.
    /// </summary>
    /// <remarks>
    /// This is the case that justifies deriving rather than storing passwords. With a password file, losing it loses the accounts;
    /// with a derivation, losing it costs one extra round trip per bot.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task APopulationWhoseFileWasLostLogsInInstead()
    {
        using (var first = new BotProvisioner(_authBase, new BotAccountStore(_accountsPath, Population), Key, concurrency: 4))
        {
            var initial = await first.EnsureAsync(Population, null, CancellationToken.None);
            Assert.That(initial.Failed, Is.Zero, initial.FirstFailure);
        }

        File.Delete(_accountsPath);

        using var second = new BotProvisioner(_authBase, new BotAccountStore(_accountsPath, Population), Key, concurrency: 4);
        var report = await second.EnsureAsync(Population, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.Zero, report.FirstFailure);
            Assert.That(report.LoggedIn, Is.EqualTo(Population), "a signup answering 409 must fall through to a login, not fail");
            Assert.That(report.Created, Is.Zero);
        });
    }

    /// <summary>A key that is not the key the accounts were made with is reported as exactly that.</summary>
    /// <remarks>
    /// The message is asserted because the symptom — a thousand 401s against accounts the file says exist — has exactly one cause
    /// and an operator should not have to deduce it.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task AWrongKeyIsReportedAsAKeyMismatch()
    {
        using (var first = new BotProvisioner(_authBase, new BotAccountStore(_accountsPath, Population), Key, concurrency: 4))
        {
            var initial = await first.EnsureAsync(Population, null, CancellationToken.None);
            Assert.That(initial.Failed, Is.Zero, initial.FirstFailure);
        }

        // STAGED, because the first pass left six perfectly good two-hour tokens and a provisioner that reuses them never reaches
        // the password at all — which is correct behaviour and made the first version of this test assert nothing. Expiring them is
        // what forces the login path the rotated key is supposed to fail on.
        var expired = new BotAccountStore(_accountsPath, Population);
        for (var i = 0; i < Population; i++)
        {
            expired.Put(expired[i] with { TokenExpUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }

        expired.Flush();

        // The file says the accounts exist, so the provisioner logs in — with a password derived from a different secret.
        using var second = new BotProvisioner(_authBase, new BotAccountStore(_accountsPath, Population), Key + "-rotated", concurrency: 4);
        var report = await second.EnsureAsync(Population, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(report.Failed, Is.EqualTo(Population));
            Assert.That(report.Complete, Is.False);
            Assert.That(report.FirstFailure, Does.Contain(BotCredentials.KeyVariable));
        });
    }

    /// <summary>
    /// <b>The invariant the whole file format exists for: no derived password reaches the disk.</b>
    /// </summary>
    /// <remarks>
    /// <b>Here, and not beside the store, because here the password genuinely exists.</b> A provisioning pass derives one per bot
    /// and posts it to a real <c>/auth/signup</c>, so searching the written file for those exact values is a test that can fail.
    /// The equivalent assertion at the store level could not: the store is never given a password, so it was asserting the absence
    /// of a value the code under test had never seen.
    /// <para>
    /// Searched as raw bytes rather than field by field, because a future field, a debugging aid or a serializer setting could put
    /// a credential in the file without any property being named one — and that is the only way this invariant ever breaks.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task NoPasswordReachesTheAccountsFile()
    {
        var store = new BotAccountStore(_accountsPath, Population);
        using (var provisioner = new BotProvisioner(_authBase, store, Key, concurrency: 4))
        {
            var report = await provisioner.EnsureAsync(Population, null, CancellationToken.None);
            Assert.That(report.Failed, Is.Zero, report.FirstFailure);
        }

        var written = await File.ReadAllTextAsync(_accountsPath);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < Population; i++)
            {
                var password = BotCredentials.Password(Key, i);
                Assert.That(password, Is.Not.Empty, "the derivation produced nothing, so the search below would prove nothing");
                Assert.That(written, Does.Not.Contain(password), $"bot {i}'s password reached the disk");
            }

            Assert.That(written, Does.Not.Contain(Key), "the fleet key itself reached the disk");
            Assert.That(written, Does.Not.Contain("password"), "the file declares a password field");

            // And it still holds what it is FOR, so this cannot be passed by writing an empty file.
            Assert.That(written, Does.Contain(BotCredentials.Name(0)), "the file must still name the accounts");
            Assert.That(written, Does.Contain("\"token\""), "the file must still carry the tokens a restart needs");
        });
    }

    /// <summary>The address Kestrel bound, since the port was asked for as zero.</summary>
    /// <param name="app">The started host.</param>
    /// <returns>Its first address.</returns>
    private static string BoundAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Assert.That(addresses?.Addresses, Is.Not.Null.And.Not.Empty, "Kestrel reported no bound address");
        foreach (var address in addresses.Addresses)
        {
            return address;
        }

        return null;
    }
}
