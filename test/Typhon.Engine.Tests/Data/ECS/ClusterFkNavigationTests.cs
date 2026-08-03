using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Target of every FK below. SingleVersion, so its own archetype is cluster-backed too.
[Component("Typhon.Test.Nav.MixGuild", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MixNavGuild
{
    [Index(AllowMultiple = true)] public int Level;
    public int Pad;

    public MixNavGuild(int level)
    {
        Level = level;
        Pad = 0;
    }
}

// VERSIONED FK source. On its own it would index on the ComponentTable — but its archetype below also holds an SV component, which makes the archetype
// cluster-eligible and moves this index onto the ARCHETYPE. That is the case the old NotSupportedException guard did not catch: it tested the component's
// storage mode, and this component is Versioned, so navigation proceeded and silently returned nothing.
[Component("Typhon.Test.Nav.MixPlayer", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct MixNavPlayer
{
    [Index(AllowMultiple = true), ForeignKey(typeof(MixNavGuild))]
    public long GuildId;
    [Index(AllowMultiple = true)] public int Score;

    public MixNavPlayer(long guildId, int score)
    {
        GuildId = guildId;
        Score = score;
    }
}

[Component("Typhon.Test.Nav.MixTag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MixNavTag
{
    public int Marker;
    public int Pad;

    public MixNavTag(int marker)
    {
        Marker = marker;
        Pad = 0;
    }
}

[Archetype]
class MixNavGuildArch : Archetype<MixNavGuildArch>
{
    public static readonly Comp<MixNavGuild> Guild = Register<MixNavGuild>();
}

// Mixed: Versioned FK source + SV sibling ⇒ cluster-eligible ⇒ the FK index lives on the archetype.
[Archetype]
class MixNavPlayerArch : Archetype<MixNavPlayerArch>
{
    public static readonly Comp<MixNavPlayer> Player = Register<MixNavPlayer>();
    public static readonly Comp<MixNavTag> Tag = Register<MixNavTag>();
}

// Same source component, but ALONE — pure Versioned, so this archetype is NOT cluster-eligible and its FK index stays on the shared ComponentTable.
// Sources of one navigation query therefore straddle both index homes.
[Archetype]
class FlatNavPlayerArch : Archetype<FlatNavPlayerArch>
{
    public static readonly Comp<MixNavPlayer> Player = Register<MixNavPlayer>();
}

/// <summary>
/// FK navigation when the source archetype keeps its indexes on the ARCHETYPE rather than the ComponentTable — issue #662.
/// </summary>
/// <remarks>
/// <para>
/// Navigation resolved its FK index with <c>PipelineExecutor.FindFKIndex(sourceCT, …)</c> — the ComponentTable tree, which is empty for a cluster-backed
/// archetype. For a pure-SingleVersion source that was a loud <c>NotSupportedException</c> (covered by <c>EcsNavigationTests</c>); for a <b>Versioned</b>
/// source in a mixed archetype the guard did not fire at all and the query returned a silently empty set.
/// </para>
/// <para>
/// The union case matters independently: the ComponentTable index spans archetypes, so a naive fix that scans it inside the per-archetype loop returns every
/// non-cluster hit once per archetype. <see cref="SourcesSplitAcrossBothIndexHomes_UnionIsCompleteWithNoDuplicates"/> is what catches that.
/// </para>
/// </remarks>
[TestFixture]
class ClusterFkNavigationTests : TestBase<ClusterFkNavigationTests>
{
    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        base.RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<MixNavGuild>();
        dbe.RegisterComponentFromAccessor<MixNavPlayer>();
        dbe.RegisterComponentFromAccessor<MixNavTag>();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Guards the premise: without these two shapes the tests below prove nothing about two index homes.</summary>
    [Test]
    public void Fixture_SourceArchetypesSpanBothIndexHomes()
    {
        using var dbe = SetupEngine();

        Assert.That(Archetype<MixNavPlayerArch>.Metadata.HasClusterIndexes, Is.True,
            "a Versioned FK source in an archetype with an SV sibling must index on the ARCHETYPE");
        Assert.That(Archetype<FlatNavPlayerArch>.Metadata.HasClusterIndexes, Is.False,
            "the same component alone must stay on the ComponentTable index");
    }

    /// <summary>AC: Versioned FK source in a mixed SV+Versioned archetype — silently empty before #662.</summary>
    [Test]
    public void VersionedFkSource_InMixedArchetype_ResolvesThroughTheArchetypeIndex()
    {
        using var dbe = SetupEngine();

        EntityId lowGuild, highGuild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            lowGuild = tx.Spawn<MixNavGuildArch>(MixNavGuildArch.Guild.Set(new MixNavGuild(10)));
            highGuild = tx.Spawn<MixNavGuildArch>(MixNavGuildArch.Guild.Set(new MixNavGuild(50)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        EntityId inLow, inHigh1, inHigh2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            inLow = tx.Spawn<MixNavPlayerArch>(MixNavPlayerArch.Player.Set(new MixNavPlayer((long)lowGuild.RawValue, 1)),
                MixNavPlayerArch.Tag.Set(new MixNavTag(0)));
            inHigh1 = tx.Spawn<MixNavPlayerArch>(MixNavPlayerArch.Player.Set(new MixNavPlayer((long)highGuild.RawValue, 2)),
                MixNavPlayerArch.Tag.Set(new MixNavTag(0)));
            inHigh2 = tx.Spawn<MixNavPlayerArch>(MixNavPlayerArch.Player.Set(new MixNavPlayer((long)highGuild.RawValue, 3)),
                MixNavPlayerArch.Tag.Set(new MixNavTag(0)));
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<MixNavPlayerArch>()
            .NavigateField<MixNavPlayer, MixNavGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(2), "both players in the high-level guild");
        Assert.That(result.Contains(inHigh1), Is.True);
        Assert.That(result.Contains(inHigh2), Is.True);
        Assert.That(result.Contains(inLow), Is.False, "the low-level guild's player must not match");
    }

    /// <summary>
    /// AC: sources split across a cluster-backed AND a non-cluster archetype — the union must be complete and duplicate-free.
    /// </summary>
    [Test]
    public void SourcesSplitAcrossBothIndexHomes_UnionIsCompleteWithNoDuplicates()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<MixNavGuildArch>(MixNavGuildArch.Guild.Set(new MixNavGuild(50)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Three sources in the CLUSTER-backed archetype, two in the FLAT one — all pointing at the same guild.
        var clusterIds = new EntityId[3];
        var flatIds = new EntityId[2];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < clusterIds.Length; i++)
            {
                clusterIds[i] = tx.Spawn<MixNavPlayerArch>(MixNavPlayerArch.Player.Set(new MixNavPlayer((long)guild.RawValue, 10 + i)),
                    MixNavPlayerArch.Tag.Set(new MixNavTag(i)));
            }
            for (var i = 0; i < flatIds.Length; i++)
            {
                flatIds[i] = tx.Spawn<FlatNavPlayerArch>(FlatNavPlayerArch.Player.Set(new MixNavPlayer((long)guild.RawValue, 20 + i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // Query the cluster-backed archetype: the archetype mask admits only those three, even though the ComponentTable tree also holds the flat two.
        using var txQ = dbe.CreateQuickTransaction();
        var clusterOnly = txQ.Query<MixNavPlayerArch>()
            .NavigateField<MixNavPlayer, MixNavGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Execute();

        Assert.That(clusterOnly.Count, Is.EqualTo(clusterIds.Length), "the mask must exclude the other archetype's sources");
        foreach (var id in clusterIds)
        {
            Assert.That(clusterOnly.Contains(id), Is.True);
        }

        // And the flat archetype resolves through the ComponentTable home, untouched by the cluster path.
        var flatOnly = txQ.Query<FlatNavPlayerArch>()
            .NavigateField<MixNavPlayer, MixNavGuild>(p => p.GuildId)
            .Where((p, g) => g.Level >= 30)
            .Execute();

        Assert.That(flatOnly.Count, Is.EqualTo(flatIds.Length));
        foreach (var id in flatIds)
        {
            Assert.That(flatOnly.Contains(id), Is.True);
        }

        // The union: a HashSet<EntityId> would hide a duplicate, so count the raw hits the reverse lookup produces. Phase 1 scans the shared ComponentTable
        // tree ONCE for all non-cluster archetypes; running it inside the per-archetype loop instead would report each flat source once per candidate.
        var hits = CountRawReverseLookupHits(dbe, (long)guild.RawValue);
        Assert.That(hits, Is.EqualTo(clusterIds.Length + flatIds.Length),
            "every source exactly once — a per-archetype rescan of the shared tree would double-count the flat sources");
    }

    /// <summary>
    /// Counts the raw callbacks the reverse lookup makes for <paramref name="targetPK"/>, without de-duplicating. A set-based assertion cannot see a
    /// duplicate; this can.
    /// </summary>
    private static int CountRawReverseLookupHits(DatabaseEngine dbe, long targetPK)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var sourceCT = dbe.GetComponentTable<MixNavPlayer>();
        var fkField = sourceCT.Definition.FieldsByName["GuildId"];
        var ordinal = PipelineExecutor.FindFKIndexOrdinal(sourceCT, fkField.OffsetInComponentStorage);
        var candidates = FkReverseLookup.ResolveCandidates(dbe, ArchetypeRegistry.GetComponentTypeId<MixNavPlayer>());

        var counter = new HitCounter();
        FkReverseLookup.ForEachSource(dbe, sourceCT, in candidates, ordinal, targetPK, ref counter);
        return counter.Count;
    }

    private struct HitCounter : IFkSourceAction
    {
        public int Count;

        public bool Process(long sourcePK, ArchetypeMetadata meta)
        {
            Count++;
            return true;
        }
    }

    /// <summary>
    /// AC: no allocation per target PK in the fan-out. <c>ReverseLookupAndUpdate</c> runs once per target delta entry, so anything rebuilt inside it — the
    /// candidate list, the FK ordinal, a closure — is rebuilt on every fan-out. Measured rather than asserted from inspection.
    /// </summary>
    [Test]
    public void ReverseLookup_AcrossManyTargets_DoesNotAllocatePerTarget()
    {
        using var dbe = SetupEngine();

        EntityId guild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            guild = tx.Spawn<MixNavGuildArch>(MixNavGuildArch.Guild.Set(new MixNavGuild(50)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 8; i++)
            {
                tx.Spawn<MixNavPlayerArch>(MixNavPlayerArch.Player.Set(new MixNavPlayer((long)guild.RawValue, i)),
                    MixNavPlayerArch.Tag.Set(new MixNavTag(i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var targetPK = (long)guild.RawValue;

        // Warm up: first call JITs the generic instantiation and touches the accessor pools.
        CountRawReverseLookupHits(dbe, targetPK);
        CountRawReverseLookupHits(dbe, targetPK);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            CountRawReverseLookupHits(dbe, targetPK);
        }
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 50;

        // The helper under test allocates nothing per target; the small residue is the fixture's own per-call setup (ResolveCandidates' array, the epoch
        // guard). A per-target rebuild of the reverse-lookup state would put this in the kilobytes.
        Assert.That(perCall, Is.LessThan(512), $"reverse lookup allocated {perCall} bytes per target PK");
    }
}
