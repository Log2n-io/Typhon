using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// A4: the character a login reads or creates, and the index it is found through.
/// </summary>
/// <remarks>
/// <para>
/// <b>The index is what these cases are really about.</b> <c>Character.Account</c> is an <c>EntityLink&lt;AccountArch&gt;</c>, and
/// nothing in the declaration says what key type the B+Tree behind it uses — the schema layer maps every entity reference to
/// <c>FieldType.Long</c>, so it is a <c>LongSingleBTree</c> and <c>EnumerateIndex</c> has to be asked for <c>long</c>. Asking for
/// the wrong one fails a cast at run time, not at compile time, and the demo's first indexed entity reference is exactly where
/// that would be discovered by a login that never finds anybody's character.
/// </para>
/// <para>
/// The second claim under test is the cascade: <see cref="Character.Account"/> declares
/// <c>OnParentDelete = CascadeAction.Delete</c>, which is what makes "an operator can delete an account" leave nothing behind.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class CharacterStoreChecks
{
    private string _dir;
    private TatooineSim _sim;

    private const string GoodPassword = "bantha-poodoo";

    /// <summary>A spawn point in the open, far from the origin so a zeroed position is not mistaken for a saved one.</summary>
    private static readonly Point3F Somewhere = new() { X = 1234.5f, Y = 7.5f, Z = -678.25f };

    /// <summary>The work factor before this fixture lowered it.</summary>
    private int _iterations;

    [SetUp]
    public void SetUp()
    {
        // The floor, not the 600 000 default: nothing here is about the hash, and at the default the two hundred signups of
        // EveryAccountResolvesToItsOwnCharacter were nine seconds of PBKDF2. The factor is stored per account, so this changes no check.
        _iterations = PasswordHash.Iterations;
        PasswordHash.Iterations = PasswordHash.MinimumIterations;

        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;
        _sim = new TatooineSim(config);
        _sim.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _sim?.Dispose();
        _sim = null;
        Worlds.Delete(_dir);
        PasswordHash.Iterations = _iterations;
    }

    private DatabaseEngine Dbe => _sim.Dbe;

    /// <summary>Signs up an account and hands back its entity.</summary>
    /// <param name="name">The account name.</param>
    /// <returns>The account.</returns>
    private EntityId Account(string name)
    {
        Assert.That(AccountStore.TrySignup(Dbe, name, GoodPassword, out var account), Is.EqualTo(AccountStore.SignupOutcome.Created));
        return account;
    }

    /// <summary>
    /// The off-tick path dresses a new character in the outfit its account chose at sign-up, as the login path does: two ways of making a
    /// character must not make two different ones.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ANewCharacterWearsTheOutfitItsAccountChose()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Mon_Mothma", GoodPassword, out var account, 0), Is.EqualTo(AccountStore.SignupOutcome.Created));
        var chosen = (byte)((TatooineData.OutfitFor(account) % TatooineData.PlayerOutfits.Length) + 1);
        Assert.That(AccountStore.TrySignup(Dbe, "Bail_Organa", GoodPassword, out var second, chosen), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assume.That(TatooineData.OutfitFor(second), Is.Not.EqualTo(chosen), "the account's own pick happens to be the chosen one");

        CharacterStore.ReadOrCreate(Dbe, second, "Bail", in Somewhere, 0, out _, out var profile);
        Assert.That(profile.Outfit, Is.EqualTo(chosen));
    }

    /// <summary>First login creates the character; the next finds it.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void FirstReadOrCreate_Creates_AndTheSecondFinds()
    {
        var account = Account("Han_Solo");

        var created = CharacterStore.ReadOrCreate(Dbe, account, "Han", in Somewhere, 3, out var character, out var profile);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True, "the first call must create");
            Assert.That(character, Is.Not.EqualTo(default(EntityId)));
            Assert.That(profile.Name.AsString, Is.EqualTo("Han"));
            Assert.That(profile.Credits, Is.EqualTo(CharacterStore.StartingCredits));
            Assert.That(profile.LastRealm, Is.EqualTo(3));
            Assert.That(profile.LastPosition.X, Is.EqualTo(Somewhere.X));
            Assert.That(profile.LastSavedUtcTicks, Is.GreaterThan(0));
        });

        // A different name and spawn offered on the second call must be IGNORED: the character already exists and this is a login,
        // not a reset. Getting that wrong would teleport a returning player to the default spawn on every login.
        var again = CharacterStore.ReadOrCreate(Dbe, account, "Somebody_Else", in Point3FZero, 0, out var same, out var found);
        Assert.Multiple(() =>
        {
            Assert.That(again, Is.False, "the second call must find, not create");
            Assert.That(same, Is.EqualTo(character));
            Assert.That(found.Name.AsString, Is.EqualTo("Han"), "an existing character must not be renamed by a login");
            Assert.That(found.LastRealm, Is.EqualTo(3), "nor moved");
            Assert.That(found.LastPosition.Z, Is.EqualTo(Somewhere.Z));
        });
    }

    /// <summary>The origin, for the "ignored on the second call" arguments above.</summary>
    private static readonly Point3F Point3FZero = default;

    /// <summary>An account with no character resolves to nothing rather than to somebody else's.</summary>
    /// <remarks>
    /// The failure this guards is an index lookup that matches too widely — a key built from the wrong bytes, or a range that is
    /// not a single key, would hand back the first character in the tree. So a second account with a character has to exist for
    /// this to mean anything.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public void AnAccountWithNoCharacter_FindsNothing()
    {
        var withOne = Account("Leia");
        var withNone = Account("Chewie");
        CharacterStore.ReadOrCreate(Dbe, withOne, "Leia", in Somewhere, 0, out var hers, out _);

        Assert.Multiple(() =>
        {
            Assert.That(CharacterStore.TryFind(Dbe, withNone, out var none, out _), Is.False, "an account with no character must find nothing");
            Assert.That(none, Is.EqualTo(default(EntityId)));
            Assert.That(CharacterStore.TryFind(Dbe, withOne, out var theirs, out _), Is.True);
            Assert.That(theirs, Is.EqualTo(hers), "and the one that has a character must resolve to its own");
        });
    }

    /// <summary>Many accounts, many characters, each resolving to exactly its own.</summary>
    /// <remarks>
    /// Enough of them to split the B+Tree's root, because a mis-strided or mis-keyed index survives a handful of entries and fails
    /// once the tree has a level.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void EveryAccountResolvesToItsOwnCharacter()
    {
        const int people = 200;
        var accounts = new EntityId[people];
        var characters = new EntityId[people];

        for (var i = 0; i < people; i++)
        {
            accounts[i] = Account($"person_{i:D4}");
            CharacterStore.ReadOrCreate(Dbe, accounts[i], $"name_{i:D4}", in Somewhere, (ushort)(i % 4), out characters[i], out _);
        }

        var wrong = new List<string>();
        for (var i = 0; i < people; i++)
        {
            if (!CharacterStore.TryFind(Dbe, accounts[i], out var found, out var profile))
            {
                wrong.Add($"{i}: not found");
            }
            else if (found != characters[i] || profile.Name.AsString != $"name_{i:D4}")
            {
                wrong.Add($"{i}: resolved {found} / {profile.Name.AsString}");
            }
        }

        Assert.That(wrong, Is.Empty);
    }

    /// <summary>
    /// Concurrent first logins for one account produce exactly one character.
    /// </summary>
    /// <remarks>
    /// <b>The lookup cannot settle this and the unique index can.</b> Every thread's <c>TryFind</c> comes back empty before any of
    /// them commits, so a probe-then-insert implementation creates several characters for one account — two bodies, two wallets, and
    /// a login that returns a different one each time. The index refuses the second insert and <c>ReadOrCreate</c> re-reads.
    /// <para>
    /// <b>Quarantined against #1258, which this is a live reproducer of.</b> A spawn's CLUSTER index entries are inserted in the
    /// publish, past the WAL append: eight racers all pass <c>ValidateUniqueKeysBeforeAppend</c> — none of their keys is in the index
    /// yet — then the first to publish inserts, and the other seven throw inside their own publish. Since the engine's AP-03
    /// hardening that is a commit-pipeline failure (<c>CommitPublishIncompleteException</c>, "the engine stopped committing"), not a
    /// <c>UniqueConstraintViolationException</c> <c>ReadOrCreate</c> can catch and re-read from. It was never sound before either —
    /// the loser's records were already in the log, so a restart resurrected its spawn — the engine was simply silent about it.
    /// Fixing it needs #1258's reservation, held from the pre-append check to the publish's insert, which is an engine change and not
    /// a demo one. <c>AccountStoreChecks.SimultaneousSignupsForOneName_ExactlyOneWins</c> is the same shape and passes only because
    /// 600k PBKDF2 iterations skew its threads far apart; it is green by timing, not by contract.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [Category("Quarantine")]
    public void ConcurrentFirstLogins_ProduceExactlyOneCharacter()
    {
        var account = Account("Lando");

        const int racers = 8;
        var ids = new EntityId[racers];
        var createdFlags = new bool[racers];
        using var start = new Barrier(racers);

        var runners = new Task[racers];
        for (var i = 0; i < racers; i++)
        {
            var slot = i;
            runners[slot] = Task.Run(() =>
            {
                start.SignalAndWait();
                createdFlags[slot] = CharacterStore.ReadOrCreate(Dbe, account, $"racer_{slot}", in Somewhere, 0, out ids[slot], out _);
            });
        }

        Task.WaitAll(runners);

        var distinct = new HashSet<EntityId>(ids);
        var creators = 0;
        foreach (var flag in createdFlags)
        {
            if (flag)
            {
                creators++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(distinct, Has.Count.EqualTo(1), "one account must have exactly one character, however many logins race");
            Assert.That(creators, Is.EqualTo(1), "and exactly one caller may be told it created it");
            Assert.That(distinct, Does.Not.Contain(default(EntityId)), "no racer may come away empty-handed");
        });
    }

    /// <summary>A saved location round-trips, and the account index still resolves afterwards.</summary>
    /// <remarks>
    /// <b>The second half is the real assertion.</b> <c>SaveLocation</c> rewrites the whole component, the indexed
    /// <c>Account</c> field included, with the same value. The commit path's unchanged-field guard (#665) is what keeps that off the
    /// B+Tree — and if it did not, A6's cadence would move the key for every live avatar on every save. A lookup that fails after a
    /// save is how that would show up.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void SaveLocation_RoundTrips_AndLeavesTheIndexAlone()
    {
        var account = Account("Ackbar");
        CharacterStore.ReadOrCreate(Dbe, account, "Ackbar", in Somewhere, 0, out var character, out var fresh);

        var moved = new Point3F { X = -4096.5f, Y = 12.25f, Z = 2048.75f };
        Assert.That(CharacterStore.SaveLocation(Dbe, character, in moved, 7), Is.True);

        Assert.That(CharacterStore.TryFind(Dbe, account, out var same, out var saved), Is.True,
            "the account index must still resolve after a position save, or A6's cadence rewrites a B+Tree key per avatar per save");

        Assert.Multiple(() =>
        {
            Assert.That(same, Is.EqualTo(character));
            Assert.That(saved.LastPosition.X, Is.EqualTo(moved.X));
            Assert.That(saved.LastPosition.Y, Is.EqualTo(moved.Y));
            Assert.That(saved.LastPosition.Z, Is.EqualTo(moved.Z));
            Assert.That(saved.LastRealm, Is.EqualTo(7));
            Assert.That(saved.LastSavedUtcTicks, Is.GreaterThanOrEqualTo(fresh.LastSavedUtcTicks));
            Assert.That(saved.Name.AsString, Is.EqualTo("Ackbar"), "and nothing else in the component moved");
            Assert.That(saved.Credits, Is.EqualTo(CharacterStore.StartingCredits));
        });
    }

    /// <summary>Saving a character that does not exist is a refusal, not a throw.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void SavingAnAbsentCharacter_IsRefused()
        => Assert.That(CharacterStore.SaveLocation(Dbe, EntityId.FromRawValue(0xDEAD_BEEF), in Somewhere, 0), Is.False);

    /// <summary>
    /// Deleting the account deletes its character, which is what the cascade declaration claims.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because the declaration is a claim, not a mechanism this demo implements.</b>
    /// <c>[Index(OnParentDelete = CascadeAction.Delete)]</c> on an <c>EntityLink</c> is the engine's cascade, and the back office's
    /// one destructive operation — delete an account — depends on it. Without it an operator leaves a character row pointing at an
    /// account that no longer exists, which the next signup reusing that id would inherit.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public void DeletingTheAccount_TakesTheCharacterWithIt()
    {
        var kept = Account("Wicket");
        var doomed = Account("Greedo");
        CharacterStore.ReadOrCreate(Dbe, kept, "Wicket", in Somewhere, 0, out var keptCharacter, out _);
        CharacterStore.ReadOrCreate(Dbe, doomed, "Greedo", in Somewhere, 0, out var doomedCharacter, out _);

        using (var tx = Dbe.CreateQuickTransaction())
        {
            tx.Destroy(doomed);
            tx.Commit();
        }

        Dbe.WriteTickFence(1);

        Assert.Multiple(() =>
        {
            Assert.That(CharacterStore.TryFind(Dbe, doomed, out _, out _), Is.False, "the deleted account's character must be gone");
            Assert.That(CharacterStore.TryFind(Dbe, kept, out var survivor, out _), Is.True, "and the other account's must not be");
            Assert.That(survivor, Is.EqualTo(keptCharacter));
            Assert.That(doomedCharacter, Is.Not.EqualTo(keptCharacter));
        });
    }
}
