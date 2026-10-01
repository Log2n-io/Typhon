using System;
using NUnit.Framework;
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
[TestFixture]
[NonParallelizable]
public sealed class ReviewFixChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Worlds.NewDirectory();

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
            new() { Kind = CombatEventKind.MissionReward, Target = cloned, Amount = 5 },
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
            Assert.That(fourth, Is.EqualTo(cloned), "a reward is not damage: a cloned player still gets paid");
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
