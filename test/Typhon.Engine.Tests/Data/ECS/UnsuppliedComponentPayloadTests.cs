using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Unsupplied.SvA", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct UnsuppliedSvA
{
    public float X, Y, Z;

    public UnsuppliedSvA(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

[Component("Typhon.Test.Unsupplied.SvB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct UnsuppliedSvB
{
    public float Dx, Dy, Dz;

    public UnsuppliedSvB(float dx, float dy, float dz)
    {
        Dx = dx;
        Dy = dy;
        Dz = dz;
    }
}

[Archetype]
partial class UnsuppliedSvUnit : Archetype<UnsuppliedSvUnit>
{
    public static readonly Comp<UnsuppliedSvA> A = Register<UnsuppliedSvA>();
    public static readonly Comp<UnsuppliedSvB> B = Register<UnsuppliedSvB>();
}

/// <summary>
/// A component the spawn never supplied must not surface a DIFFERENT entity's data (#845).
/// </summary>
/// <remarks>
/// <para>
/// Both tests deliberately force a chunk RECYCLE rather than reading a fresh allocation: spawn with a distinctive
/// pattern, destroy, drain, then spawn again omitting one component. A fresh chunk is zero for uninteresting reasons —
/// the operating system hands out zeroed pages — so a test that skips the recycle passes whether or not the engine
/// clears anything, which is why this went unnoticed.
/// </para>
/// <para>
/// An unsupplied component is left DISABLED, so <c>Read</c> throws "Component at slot N is disabled". <c>Enable()</c> is
/// the reachable path and does not initialise the payload, which is what makes the stale bytes observable through the
/// ordinary public API.
/// </para>
/// </remarks>
[NonParallelizable]
class UnsuppliedComponentPayloadTests : TestBase<UnsuppliedComponentPayloadTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<UnsuppliedSvA>();
        dbe.RegisterComponentFromAccessor<UnsuppliedSvB>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// SingleVersion: clean, because #839 stages non-Versioned payloads in an arena that clears each slot at Alloc.
    /// </summary>
    [Test]
    public void SingleVersion_UnsuppliedComponent_IsZero_NotThePreviousOccupant()
    {
        using var dbe = SetupEngine();

        EntityId a;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var x = new UnsuppliedSvA(111, 222, 333);
            var y = new UnsuppliedSvB(444, 555, 666);
            a = tx.Spawn<UnsuppliedSvUnit>(UnsuppliedSvUnit.A.Set(in x), UnsuppliedSvUnit.B.Set(in y));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(a);
            tx.Commit();
        }
        dbe.FlushDeferredCleanups();

        EntityId b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var x = new UnsuppliedSvA(1, 2, 3);
            b = tx.Spawn<UnsuppliedSvUnit>(UnsuppliedSvUnit.A.Set(in x));
            tx.Commit();
        }

        using (var en = dbe.CreateQuickTransaction())
        {
            en.OpenMut(b).Enable(UnsuppliedSvUnit.B);
            en.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(b).Read(UnsuppliedSvUnit.B);
        var v = vr;   // ref readonly locals cannot be captured by the lambda below

        Assert.Multiple(() =>
        {
            Assert.That(v.Dx, Is.Zero, "a component this spawn never supplied must not carry the destroyed entity's X");
            Assert.That(v.Dy, Is.Zero, "…nor its Y");
            Assert.That(v.Dz, Is.Zero, "…nor its Z");
        });
    }

    /// <summary>
    /// Versioned: currently returns the destroyed entity's values (#845).
    /// </summary>
    /// <remarks>
    /// Quarantined, not deleted, and not weakened to assert the broken behaviour. The spawn path allocates a chunk per
    /// Versioned slot whether or not a value was supplied, and allocates it with <c>clearContent: false</c>
    /// (<c>Transaction.ECS.cs:348, :473, :601</c>); the value copy runs only for supplied slots, so an unsupplied one
    /// keeps whatever the recycled chunk's previous owner left there. The sibling test above is the same scenario on
    /// SingleVersion and passes, which is what makes this a storage-mode DISAGREEMENT rather than a documented
    /// "undefined content" contract.
    /// </remarks>
    [Test]
    [Category("Quarantine")]
    public void Versioned_UnsuppliedComponent_IsZero_NotThePreviousOccupant()
    {
        using var dbe = SetupEngine();

        EntityId a;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(111, 222, 333);
            var vel = new EcsVelocity(444, 555, 666);
            a = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(a);
            tx.Commit();
        }
        dbe.FlushDeferredCleanups();

        EntityId b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            b = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            tx.Commit();
        }

        using (var en = dbe.CreateQuickTransaction())
        {
            en.OpenMut(b).Enable(EcsUnit.Velocity);
            en.Commit();
        }

        using var read = dbe.CreateQuickTransaction();
        ref readonly var vr = ref read.Open(b).Read(EcsUnit.Velocity);
        var v = vr;   // ref readonly locals cannot be captured by the lambda below

        Assert.Multiple(() =>
        {
            Assert.That(v.Dx, Is.Zero, "#845: reads 444 — the destroyed entity A's velocity, on entity B");
            Assert.That(v.Dy, Is.Zero, "#845: reads 555");
            Assert.That(v.Dz, Is.Zero, "#845: reads 666");
        });
    }
}
