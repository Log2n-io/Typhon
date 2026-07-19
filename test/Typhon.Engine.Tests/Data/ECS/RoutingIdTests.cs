using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Feature #514 Phase 1 — per-DB archetype routing id. The routing id is engine-assigned (dense, from 1), embedded in every
// EntityId (low 16 bits), persisted in ArchetypeR1.RoutingId, and restored by NAME on reopen. Author-set [Archetype(Id=N)]
// remains the per-process catalog id (an internal handle); it is NOT what EntityId carries.
[Component("Typhon.Test.Routing.Val", 1)]
[StructLayout(LayoutKind.Sequential)]
struct RoutingVal
{
    public int V;
    public int W; // padding — a component's storage must total >= 8 bytes
    public RoutingVal(int v) { V = v; W = 0; }
}

[Archetype(990)]
partial class RoutingArchA : Archetype<RoutingArchA>
{
    public static readonly Comp<RoutingVal> Val = Register<RoutingVal>();
}

[Archetype(991)]
partial class RoutingArchB : Archetype<RoutingArchB>
{
    public static readonly Comp<RoutingVal> Val = Register<RoutingVal>();
}

[TestFixture]
[NonParallelizable]
class RoutingIdTests : TestBase<RoutingIdTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<RoutingArchA>.Touch();
        Archetype<RoutingArchB>.Touch();
    }

    private DatabaseEngine NewSession(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RoutingVal>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // AC1.2 / AC1.6 — routing ids are engine-assigned, reserved-0, distinct per archetype, and embedded in EntityId.
    [Test]
    public void RoutingIds_AreReservedZero_Distinct_AndEmbeddedInEntityId()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = NewSession(scope);

        var rA = dbe.RoutingIdOf(Archetype<RoutingArchA>.Metadata);
        var rB = dbe.RoutingIdOf(Archetype<RoutingArchB>.Metadata);

        Assert.That(rA, Is.GreaterThanOrEqualTo(1), "routing id 0 is reserved (EntityId.Null / null-sentinel)");
        Assert.That(rB, Is.GreaterThanOrEqualTo(1));
        Assert.That(rA, Is.Not.EqualTo(rB), "two distinct archetypes get distinct routing ids — the pluggability seed");

        using var t = dbe.CreateQuickTransaction();
        var idA = t.Spawn<RoutingArchA>(RoutingArchA.Val.Set(new RoutingVal(1)));
        var idB = t.Spawn<RoutingArchB>(RoutingArchB.Val.Set(new RoutingVal(2)));

        // EntityId carries the per-DB routing id, NOT the author catalog id (950/951).
        Assert.That(idA.ArchetypeId, Is.EqualTo(rA));
        Assert.That(idB.ArchetypeId, Is.EqualTo(rB));
    }

    // AC1.4 — reopen matches persisted archetype rows by NAME and restores the routing id, so pre-existing EntityIds resolve.
    [Test]
    public void Reopen_RestoresRoutingIdByName_AndEntityResolves()
    {
        EntityId idA;
        ushort routingA;

        // Session 1 — spawn, record the routing id, dispose cleanly (persists ArchetypeR1.RoutingId).
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = NewSession(scope))
        {
            routingA = dbe.RoutingIdOf(Archetype<RoutingArchA>.Metadata);
            using var t = dbe.CreateQuickTransaction();
            idA = t.Spawn<RoutingArchA>(RoutingArchA.Val.Set(new RoutingVal(42)));
            Assert.That(t.Commit(), Is.True);
        }

        // Session 2 — reopen the same DB; routing id must be restored by name, and the session-1 EntityId must resolve.
        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = NewSession(scope2);

        Assert.That(dbe2.RoutingIdOf(Archetype<RoutingArchA>.Metadata), Is.EqualTo(routingA),
            "routing id must be restored (by name) to the same value across reopen");

        using var t2 = dbe2.CreateReadOnlyTransaction();
        var e = t2.Open(idA);
        Assert.That(e.IsValid, Is.True, "an EntityId spawned before reopen must still resolve after reopen");
        Assert.That(e.Read(RoutingArchA.Val).V, Is.EqualTo(42), "component data survives reopen and resolves via the restored routing id");
    }
}
