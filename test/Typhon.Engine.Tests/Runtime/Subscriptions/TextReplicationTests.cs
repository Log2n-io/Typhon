using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>A memo of up to 1 023 bytes: a <see cref="String1024"/>, which a replicated archetype can carry since its text moved out of line (#1094).</summary>
[Component("Typhon.Test.Proj.Memo", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjMemo
{
    [Field]
    public String1024 Memo;

    [Field]
    public int Revision;
}

[Archetype]
partial class ProjMemoed : Archetype<ProjMemoed>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjMemo> Memo = Register<ProjMemo>();
}

/// <summary>A <see cref="String1024"/> field projects as <c>str{≤1023}</c> end to end, read through its chunk (#1094, #1085 I-4).</summary>
[TestFixture]
[NonParallelizable]
sealed class TextReplicationTests : TestBase<TextReplicationTests>
{
    private const string Profile = "world";

    private static DatabaseEngine Engine(System.IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjMemo>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new System.Numerics.Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new System.Numerics.Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjMemoed>(a => a
            .Motion(ProjMemoed.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjMemoed.Memo, x => x.Memo, Codec.Exact, name: "memo", group: "memo")
            .Field(ProjMemoed.Memo, x => x.Revision, Codec.Exact, name: "rev", group: "memo"));
        subs.Profile(Profile, p => p.World().Of<ProjMemoed>());
    }

    private static void SetMemo(DatabaseEngine dbe, EntityId id, string text, int revision, FrameHarness harness = null)
    {
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        var v = e.Read(ProjMemoed.Memo);
        v.Revision = revision;
        e.Set(ProjMemoed.Memo, v);
        e.SetText(ProjMemoed.Memo, ref v, ref v.Memo, text);
        harness?.Subscriptions.Commands.Replicate(in e);   // explicit replication: a write is sent once pushed (ADR-067)
        tx.Commit();
    }

    [Test]
    public void AString1024ReachesTheReplica_AndAChangedOneIsResent()
    {
        var dbe = Engine(ServiceProvider);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = 9.5f, MinY = -0.5f, MaxX = 10.5f, MaxY = 0.5f }, Speed = 1f };
            id = tx.Spawn<ProjMemoed>(ProjMemoed.Bounds.Set(in bounds), ProjMemoed.Memo.Set(new ProjMemo()));
            tx.Commit();
        }

        // 600 'é' are 1 200 bytes: stored cut at 1 023 bytes on a code point, i.e. 511 of them.
        SetMemo(dbe, id, new string('é', 600), 1);

        using var harness = FrameHarness.Create(dbe, Declare, nameof(TextReplicationTests), replicationCellM: ProjectionTestSchema.ReplicationCellFor(0));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, Profile)[0];
        harness.RunTick(1);
        Assert.That(harness.Deliver(session), Is.EqualTo(1));
        var archetype = harness.CatalogPlan.ArchetypeByName(nameof(ProjMemoed)).Idx;
        var replica = harness.Replica(session);
        var netId = replica.NetIds(archetype).Single();
        Assert.That(replica.Text(archetype, netId, "memo"), Is.EqualTo(new string('é', 511)), "the text, read through its chunk");

        // Rewritten in place: the handle in the component does not change, the text does — and the replica follows.
        SetMemo(dbe, id, "short now", 2, harness);
        harness.RunTick(2);
        harness.Deliver(session);
        Assert.That(replica.Value(archetype, netId, "rev"), Is.EqualTo(2));
        Assert.That(replica.Text(archetype, netId, "memo"), Is.EqualTo("short now"),
            "the handle is the same, the text is not: the body is compared, not the handle");
        Assert.That(replica.Store.Anomalies, Is.Zero);
    }
}
