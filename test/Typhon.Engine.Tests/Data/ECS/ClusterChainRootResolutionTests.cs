using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>QueryRead</c> must resolve each entity's OWN revision chain on a cluster-backed archetype (#629).
/// </summary>
/// <remarks>
/// <para>
/// The two entity-record shapes put different things at byte 14. A flat record has <c>Location[0]</c> there; a cluster record has <c>ClusterChunkId</c>, and
/// the chain roots live at <c>CompRevOffset</c> (19), indexed by VERSIONED ordinal rather than by component slot.
/// <c>Transaction.ResolveEntityMapSlotChunkId</c> read every record with the flat accessor, so on a cluster archetype it returned the cluster chunk id in
/// place of the chain root.
/// </para>
/// <para>
/// That value is <b>shared by every entity in the same cluster</b> — up to 64 of them — so they all resolved to one arbitrary entity's component. Reads
/// succeeded and returned plausible data belonging to a different entity, which is the worst shape a defect can take: no exception, no empty result, just
/// wrong answers. Entities spawned in separate transactions usually landed in different clusters and looked fine, which is why the whole class of failure
/// only showed up in fixtures that spawn a batch.
/// </para>
/// <para>
/// Every <c>Where&lt;T&gt;(lambda)</c> secondary-component filter and every navigation predicate goes through this resolver, so the blast radius was far
/// wider than the navigation tests that happened to catch it.
/// </para>
/// </remarks>
[TestFixture]
class ClusterChainRootResolutionTests : TestBase<ClusterChainRootResolutionTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>AC: entities spawned together in one transaction each read back their own component through <c>QueryRead</c>.</summary>
    /// <remarks>
    /// One transaction and enough entities to fill more than a single cluster. A pair would pass by luck the moment the geometry changed; sharing a cluster is
    /// the precondition for the bug, so the fixture has to guarantee it rather than hope for it.
    /// </remarks>
    [Test]
    public void QueryRead_EntitiesSharingACluster_EachResolvesItsOwnChain()
    {
        using var dbe = SetupEngine();

        const int count = 100;
        var ids = new EntityId[count];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(i, i * 10)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    Assert.That(tx.QueryRead<CompGuild>((long)ids[i].RawValue, out var g), Is.True, $"entity {i} must be readable");
                    Assert.That(g.Level, Is.EqualTo(i), $"entity {i} must read its OWN Level, not a cluster-mate's");
                    Assert.That(g.MemberCap, Is.EqualTo(i * 10), $"entity {i} must read its OWN MemberCap");
                }
            });
        }
    }

    /// <summary>AC: the same, through <c>Open().TryRead</c> — the path that was already correct, kept so a fix cannot trade one for the other.</summary>
    [Test]
    public void OpenTryRead_EntitiesSharingACluster_EachResolvesItsOwnChain()
    {
        using var dbe = SetupEngine();

        const int count = 100;
        var ids = new EntityId[count];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(i, i * 10)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    Assert.That(tx.Open(ids[i]).TryRead<CompGuild>(out var g), Is.True, $"entity {i} must be readable");
                    Assert.That(g.Level, Is.EqualTo(i), $"entity {i} must read its OWN Level");
                }
            });
        }
    }

    /// <summary>
    /// AC: a navigation predicate over a cluster-backed target filters on each target's own data.
    /// </summary>
    /// <remarks>
    /// The end-to-end shape that first exposed the defect. Both sides are cluster-backed and both predicates run through the same resolver, so a regression
    /// shows up here as a wrong count rather than as an error.
    /// </remarks>
    [Test]
    public void NavigateField_OverClusterBackedArchetypes_FiltersOnEachEntitysOwnData()
    {
        using var dbe = SetupEngine();

        EntityId lowGuild, highGuild;
        using (var tx = dbe.CreateQuickTransaction())
        {
            lowGuild = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(10, 50)));
            highGuild = tx.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(new CompGuild(50, 100)));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)lowGuild.RawValue, true)));
            tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)highGuild.RawValue, true)));
            tx.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(new CompPlayer((long)highGuild.RawValue, false)));
            tx.Commit();
        }

        using var txQ = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(txQ.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId).Execute(), Has.Count.EqualTo(3),
                "no predicate: every player reaches a guild");
            Assert.That(txQ.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
                .Where((p, g) => g.Level >= 30).Execute(), Has.Count.EqualTo(2), "target predicate: the two players in the high guild");
            Assert.That(txQ.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
                .Where((p, g) => p.Active == 1).Execute(), Has.Count.EqualTo(2), "source predicate: the two active players");
            Assert.That(txQ.Query<CompPlayerArch>().NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
                .Where((p, g) => p.Active == 1 && g.Level >= 30).Execute(), Has.Count.EqualTo(1), "both: the one active player in the high guild");
        });
    }
}
