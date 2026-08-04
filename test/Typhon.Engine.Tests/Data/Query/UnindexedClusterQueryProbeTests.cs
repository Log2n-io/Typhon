using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>Versioned, with NO indexed field — so its archetype gets no index slot at all.</summary>
[Component("Typhon.Test.UnixCluster.Plain", 1)]
[StructLayout(LayoutKind.Sequential)]
struct UqPlain
{
    public int Value;
    public int Payload;

    public UqPlain(int value, int payload) { Value = value; Payload = payload; }
}

/// <summary>Versioned, indexed — the control.</summary>
[Component("Typhon.Test.UnixCluster.Keyed", 1)]
[StructLayout(LayoutKind.Sequential)]
struct UqKeyed
{
    [Index(AllowMultiple = true)] public int Value;
    public int Payload;

    public UqKeyed(int value, int payload) { Value = value; Payload = payload; }
}

[Archetype]
class UqArch : Archetype<UqArch>
{
    public static readonly Comp<UqPlain> Plain = Register<UqPlain>();
    public static readonly Comp<UqKeyed> Keyed = Register<UqKeyed>();
}

/// <summary>
/// Guards the upstream rejection that keeps the cluster query classification safe (#629).
/// </summary>
/// <remarks>
/// <para>
/// <c>ScanAllArchetypes</c> decides between the cluster path and the shared <c>PipelineExecutor</c> path on whether <c>FindClusterIndexSlot</c> finds a slot,
/// and that only ever matches a component owning an index. An unindexed where-component would therefore return -1, get classified as non-cluster, and be sent
/// to a full scan over the ComponentTable — the flat home, which a cluster-backed archetype no longer populates. The result would be an EMPTY query rather
/// than an error: the #663 shape again.
/// </para>
/// <para>
/// It does not happen, because <c>QueryResolverHelper.ResolveEvaluators</c> rejects an unindexed field before any of that runs. That rejection is load-bearing
/// for correctness now, not just a usability nicety, so it is worth a test that says so — relaxing it to "fall back to a scan" would silently open the empty-
/// result path.
/// </para>
/// </remarks>
[TestFixture]
class UnindexedClusterQueryProbeTests : TestBase<UnindexedClusterQueryProbeTests>
{
    private const int EntityCount = 200;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<UqPlain>();
        dbe.RegisterComponentFromAccessor<UqKeyed>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>AC: an indexed where-component resolves through the per-archetype tree; an unindexed one is rejected loudly, never answered with an empty set.</summary>
    [Test]
    public void FieldPredicate_IndexedResolves_UnindexedIsRejectedNotSilentlyEmpty()
    {
        using var dbe = SetupEngine();

        Assert.That(ArchetypeRegistry.GetMetadata<UqArch>().IsClusterEligible, Is.True, "premise: every archetype is cluster-backed");

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                tx.Spawn<UqArch>(UqArch.Plain.Set(new UqPlain(i, i)), UqArch.Keyed.Set(new UqKeyed(i, i)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<UqArch>().WhereField<UqKeyed>(x => x.Value < 50).Count(), Is.EqualTo(50),
                "an indexed where-component takes the per-archetype B+Tree path and must return every match");

            // The distinction that matters: THROWS, not "returns 0". A zero here would mean the flat-home fallback had become reachable.
            Assert.That(() => tx.Query<UqArch>().WhereField<UqPlain>(x => x.Value < 50).Count(),
                Throws.InstanceOf<System.InvalidOperationException>().With.Message.Contains("not indexed"),
                "an unindexed where-component must be rejected at evaluator resolution, never routed to a scan over the empty flat home");
        }
    }
}
