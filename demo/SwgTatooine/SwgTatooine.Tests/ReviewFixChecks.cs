using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Three defects a review over the branch found, each of which was reachable and none of which showed as a failure.
/// </summary>
/// <remarks>
/// They have nothing in common except that: a throw from the tick path on a reopened world, a blow landing on a player
/// that had already been cloned out from under it, and a constructor that dereferenced the argument it was about to
/// null-check. Kept in one fixture so the reason each exists stays attached to it.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class ReviewFixChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Worlds.NewDirectory();

    [TearDown]
    public void TearDown() => Worlds.Delete(_dir);

    /// <summary>A rebuild refuses an index that is already filled, rather than appending a second of everything.</summary>
    /// <remarks>
    /// "Must be empty" was a doc comment and nothing else. A second call appends a second copy of every city, POI,
    /// shuttleport, door and player, and every consequence is quiet rather than loud: <c>PickCityIndex</c> walks a weight
    /// list that now sums to 2 and returns early, so half the cities stop being reachable; <c>Index.Players</c> doubles,
    /// so one dungeon can draw the same player into its party twice; and the door-count check throws a message blaming
    /// the map for a doubled count of its own making.
    /// </remarks>
    [Test]
    public void ARebuildRefusesAnIndexThatIsAlreadyFilled()
    {
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();

        WorldIndex index = new();
        WorldRebuild.Rebuild(sim.Dbe, sim.Map, index, realm: 0);
        Assert.That(index.Cities, Is.Not.Empty, "the premise: a first rebuild fills the index");

        var again = Assert.Throws<InvalidOperationException>(() => WorldRebuild.Rebuild(sim.Dbe, sim.Map, index, realm: 0));
        Assert.That(again!.Message, Does.Contain("empty"));
    }

    /// <summary>
    /// <c>--no-crowd</c> (Loïc, 2026-11-04): a world built without the scripted crowd has none and runs; a world saved with one reopens without it.
    /// </summary>
    [Test]
    public void WithoutTheCrowd_ABuildHasNone_ARunTicks_AndAReopenDropsTheSavedOne()
    {
        SimConfig crowded = Worlds.Small(_dir);
        using (TatooineSim sim = new(crowded))
        {
            sim.Initialize();
            Assert.That(Players(sim.Dbe), Is.Not.Zero, "the premise: a crowd was built");

            sim.Map.Config.Crowd = false;
            WorldIndex index = new();
            var census = WorldRebuild.Rebuild(sim.Dbe, sim.Map, index, realm: 0);
            Assert.Multiple(() =>
            {
                Assert.That(Players(sim.Dbe), Is.Zero, "the saved crowd still stands");
                Assert.That(index.Players, Is.Empty);
                Assert.That(census.Players, Is.Zero);
            });
        }

        Worlds.Delete(_dir);
        _dir = Worlds.NewDirectory();
        SimConfig empty = Worlds.Small(_dir);
        empty.Crowd = false;
        empty.WarmTicks = 2;
        empty.MeasuredTicks = 20;
        using TatooineSim bare = new(empty);
        bare.Initialize();
        Assert.That(Players(bare.Dbe), Is.Zero, "a crowd was built");
        Assert.DoesNotThrow(() => bare.Run());
    }

    /// <summary>
    /// A session's avatar saved with the world is destroyed on reopen, not handed to the crowd: every restart under load used to grow the crowd by every
    /// session connected at the save (32 911 bodies on a world built with 1 600).
    /// </summary>
    [Test]
    public void ASavedAvatar_IsDestroyedOnReopen_NotHandedToTheCrowd()
    {
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();
        var crowd = Players(sim.Dbe);
        EntityId avatar;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            avatar = EntityId.Null;
            foreach (var p in tx.Query<Player>())
            {
                avatar = p.Id;
                break;
            }

            var body = tx.OpenMut(avatar);
            var control = body.Read(Player.Control);
            control.Kind = ControllerKind.Human;
            body.Set(Player.Control, control);
            tx.Commit();
        }

        WorldIndex index = new();
        var census = WorldRebuild.Rebuild(sim.Dbe, sim.Map, index, realm: 0);
        using var check = sim.Dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(check.TryOpen(avatar, out _), Is.False, "the saved avatar is still there");
            Assert.That(Players(sim.Dbe), Is.EqualTo(crowd - 1), "the crowd lost or gained bodies");
            Assert.That(census.Players, Is.EqualTo(crowd - 1));
            Assert.That(index.Players, Does.Not.Contain(avatar));
        });
    }

    private static int Players(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var n = 0;
        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            n += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
        }

        return n;
    }

    /// <summary>A blow queued behind a fatal one is withdrawn when that fatal one cloned the player.</summary>
    /// <remarks>
    /// <para>
    /// Cloning restores full health, so the <c>Health &lt;= 0</c> guard that stops two shooters both killing one target
    /// stops protecting it. Two creatures push lethal damage on the same tick: the first incapacitates the player and
    /// teleports it to Mos Eisley at full health, and the second — drained next, finding health above zero — subtracts
    /// from a player now kilometres from whatever shot it, with a Strike drawn between the two.
    /// </para>
    /// <para>
    /// Staged against the withdrawal itself. Two lethal events for one player in one drain is precisely the kind of
    /// precondition a running world produces rarely and unrepeatably, and a case that waited for one would be a case
    /// that passes because the world was quiet.
    /// </para>
    /// </remarks>
    [Test]
    public void ABlowBehindAFatalOneIsWithdrawnWhenThePlayerWasCloned()
    {
        // Two real ids, because EntityId is the engine's to mint and a demo test may not fabricate one.
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();
        (EntityId cloned, EntityId other) = TwoPlayers(sim);

        Span<CombatEvent> events =
        [
            new() { Kind = CombatEventKind.Damage, Target = cloned, Amount = 900 },
            new() { Kind = CombatEventKind.Damage, Target = cloned, Amount = 900 },
            new() { Kind = CombatEventKind.Damage, Target = other, Amount = 50 },
            new() { Kind = CombatEventKind.Revive, Target = cloned },
        ];

        long withdrawn = SimBridge.WithdrawAfterClone(events, from: 1, n: 4, cloned);
        bool withdrawnIsNull = events[1].Target.IsNull;
        EntityId first = events[0].Target;
        EntityId third = events[2].Target;
        EntityId fourth = events[3].Target;

        Assert.Multiple(() =>
        {
            Assert.That(withdrawn, Is.EqualTo(1), "one later Damage named the cloned player");
            Assert.That(withdrawnIsNull, Is.True, "withdrawn: the drain counts it stale rather than applying it");
            Assert.That(first, Is.EqualTo(cloned), "the event that DID the cloning is behind `from` and untouched");
            Assert.That(third, Is.EqualTo(other), "another player's blow is not collateral");
            Assert.That(fourth, Is.EqualTo(cloned), "a revival is not damage: only blows are withdrawn");
        });
    }

    /// <summary>The bridge checks its arguments before it uses them.</summary>
    /// <remarks>
    /// The terrain field was taken from <c>config.ContentScale</c> on the line ABOVE the three
    /// <c>ArgumentNullException.ThrowIfNull</c> calls, so a null config produced a <see cref="NullReferenceException"/> —
    /// the one exception a guarded constructor exists to make impossible.
    /// </remarks>
    [Test]
    public void TheBridgeGuardsItsArgumentsBeforeReadingThem()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new SimBridge(null, null, null));
    }

    /// <summary>Any two distinct players on the planet.</summary>
    private static (EntityId First, EntityId Second) TwoPlayers(TatooineSim sim)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[64];
        var e = sim.Dbe.ClusterSpatialQuery<Player>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got = e.Fill(buffer);
            Assert.That(got, Is.GreaterThan(1), "the premise: a small world holds more than one player");
            return (buffer[0].Entity, buffer[1].Entity);
        }
        finally
        {
            e.Dispose();
        }
    }
}
