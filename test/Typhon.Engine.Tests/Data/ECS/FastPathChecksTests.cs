using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.FastPath.V", 1)] // Versioned (default)
struct FpV
{
    [Field] public long Value;   // a Versioned chunk is at least 8 bytes
}

[Component("Typhon.Test.FastPath.Sv", 1, StorageMode = StorageMode.SingleVersion)]
struct FpSv
{
    [Field] public int Value;
}

[Component("Typhon.Test.FastPath.Text", 1, StorageMode = StorageMode.SingleVersion)]
struct FpText
{
#pragma warning disable CS0649 // only ever default: the refusals are about the handle field's presence
    [Field] public String1024 Title;
#pragma warning restore CS0649
}

[Component("Typhon.Test.FastPath.VText", 1)] // Versioned, with a text
struct FpVText
{
    [Field] public String1024 Title;
#pragma warning disable CS0649
    [Field] public int Pad;   // a Versioned chunk is at least 8 bytes
#pragma warning restore CS0649
}

[Archetype]
partial class FpUnit : Archetype<FpUnit>
{
    public static readonly Comp<FpV> V = Register<FpV>();
    public static readonly Comp<FpSv> Sv = Register<FpSv>();
    public static readonly Comp<FpText> Text = Register<FpText>();
    public static readonly Comp<FpVText> VText = Register<FpVText>();
}

[Component("Typhon.Test.FastPath.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct FpPos
{
    [Field] [SpatialIndex] public AABB2F Bounds;
}

[Component("Typhon.Test.FastPath.Realm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct FpRealm
{
    [Field] [RealmKey] public ushort Realm;
}

[Archetype]
partial class FpSpatialUnit : Archetype<FpSpatialUnit>
{
    public static readonly Comp<FpPos> Pos = Register<FpPos>();
    public static readonly Comp<FpRealm> Realm = Register<FpRealm>();
    public static readonly Comp<FpV> V = Register<FpV>();
    public static readonly Comp<FpSv> Sv = Register<FpSv>();
}

/// <summary>
/// #1652: the cluster fast path's refusals hold in every build, with strict mode OFF — the configuration the Release NuGet ships. Before, each of these was
/// either behind <see cref="CheckConfig.Enabled"/> (off by default, so the JIT deleted it) or missing, and the misuse corrupted silently.
/// </summary>
/// <remarks><c>[Category("ChecksOffGated")]</c>: this suite's <c>typhon.telemetry.json</c> turns strict mode on, so the gate runs this category in a process of
/// its own with <c>TYPHON__CHECKS__ENABLED=false</c>; a case asserting a throw is evidence only there.</remarks>
[NonParallelizable]
[Category("ChecksOffGated")]
class FastPathChecksTests : TestBase<FastPathChecksTests>
{
    private static SpatialGridConfig Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    private static FpPos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<FpV>();
        dbe.RegisterComponentFromAccessor<FpSv>();
        dbe.RegisterComponentFromAccessor<FpText>();
        dbe.RegisterComponentFromAccessor<FpVText>();
        dbe.RegisterComponentFromAccessor<FpPos>();
        dbe.RegisterComponentFromAccessor<FpRealm>();
        dbe.ConfigureRealms(2);
        dbe.ConfigureSpatialGrid(Grid());
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnUnit(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<FpUnit>(FpUnit.V.Set(new FpV { Value = 1 }), FpUnit.Sv.Set(new FpSv { Value = 1 }), FpUnit.Text.Set(new FpText()));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    private static EntityId SpawnSpatial(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<FpSpatialUnit>(FpSpatialUnit.Pos.Set(At(50, 50)), FpSpatialUnit.V.Set(new FpV { Value = 1 }),
            FpSpatialUnit.Sv.Set(new FpSv { Value = 1 }));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    /// <summary>Runs <paramref name="act"/> once per cluster and returns the refusal messages (a ClusterRef is a ref struct: no lambda can hold it).</summary>
    private delegate void ClusterAct<TArch>(ClusterRef<TArch> cluster) where TArch : class;

    private static string RefusalOn<TArch>(DatabaseEngine dbe, ClusterAct<TArch> act) where TArch : class
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<TArch>())
        {
            try
            {
                act(cluster);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

#pragma warning disable TYPHON009 // GetSpan / Get: the refusals are the subject

    [Test]
    [VerifiesRule("ACCESS-01")]
    public void GetSpanAndGet_OnAVersionedComponent_AreRefused()
    {
        using var dbe = SetupEngine();
        SpawnUnit(dbe);
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.GetSpan(FpUnit.V)), Does.Contain("Versioned"));
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.Get(FpUnit.V, 0)), Does.Contain("Versioned"));
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.GetSpan(FpUnit.Sv)[0].Value = 2), Is.Null, "a SingleVersion span stays legal");
    }

    [Test]
    [VerifiesRule("TXT-02")]
    public void Get_OnAComponentWithAText_IsRefused()
    {
        using var dbe = SetupEngine();
        SpawnUnit(dbe);
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.Get(FpUnit.Text, 0)), Does.Contain("String1024"));
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.GetSpan(FpUnit.Text)), Does.Contain("String1024"));
    }

    [Test]
    [VerifiesRule("TXT-02")]
    public void EnableWithAValue_OfANeverSuppliedVersionedComponent_CarryingAnotherEntitysText_IsRefused()
    {
        using var dbe = SetupEngine();
        EntityId donor, target;
        using (var tx = dbe.CreateQuickTransaction())
        {
            donor = tx.Spawn<FpUnit>(FpUnit.V.Set(new FpV()), FpUnit.VText.Set(new FpVText()));
            target = tx.Spawn<FpUnit>(FpUnit.V.Set(new FpV()));   // VText never supplied
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(donor);
            var v = e.Read(FpUnit.VText);
            e.SetText(FpUnit.VText, ref v, ref v.Title, "the donor's");
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var stolen = tx.Open(donor).Read(FpUnit.VText);   // carries the donor's text handle
            var ex = Assert.Throws<InvalidOperationException>(() => tx.OpenMut(target).Enable(FpUnit.VText, stolen));
            Assert.That(ex.Message, Does.Contain("another entity"));
            tx.OpenMut(target).Enable(FpUnit.VText, new FpVText());   // an empty value supplies it
            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(donor).ReadText(FpUnit.VText, tx.Open(donor).Read(FpUnit.VText).Title), Is.EqualTo("the donor's"));
            Assert.That(tx.Open(target).IsEnabled(FpUnit.VText), Is.True);
        }
    }

    [Test]
    [VerifiesRule("CA-03")]
    public void WriteSpatial_WithANonSpatialOrVersionedComponent_IsRefused()
    {
        using var dbe = SetupEngine();
        SpawnSpatial(dbe);
        SpawnUnit(dbe);
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.WriteSpatial(FpSpatialUnit.Sv, 0, new FpSv { Value = 3 })), Does.Contain("[SpatialIndex]"));
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.WriteSpatial(FpSpatialUnit.V, 0, new FpV { Value = 3 })), Does.Contain("Versioned"));
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.WriteSpatial(FpSpatialUnit.Sv, 1UL, [new FpSv { Value = 3 }])), Does.Contain("[SpatialIndex]"));
        Assert.That(RefusalOn<FpUnit>(dbe, c => c.WriteSpatial(FpUnit.Sv, 0, new FpSv())), Does.Contain("[SpatialIndex]"), "an archetype with none");
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.WriteSpatial(FpSpatialUnit.Pos, 0, At(51, 51))), Is.Null, "the spatial component writes");
    }

    [Test]
    [VerifiesRule("RM-05")]
    public void GetSpanAndGet_OnTheRealmKey_AreRefused()
    {
        using var dbe = SetupEngine();
        SpawnSpatial(dbe);
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.GetSpan(FpSpatialUnit.Realm)), Does.Contain("RM-05"));
        Assert.That(RefusalOn<FpSpatialUnit>(dbe, c => c.Get(FpSpatialUnit.Realm, 0)), Does.Contain("RM-05"));
    }

    [Test]
    [VerifiesRule("CA-01")]
    public void Set_OfANonFinitePosition_IsRefusedAtTheCall()
    {
        using var dbe = SetupEngine();
        var id = SpawnSpatial(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.Throws<InvalidOperationException>(() => tx.OpenMut(id).Set(FpSpatialUnit.Pos, At(float.NaN, 50)));
            Assert.Throws<InvalidOperationException>(() => tx.OpenMut(id).Set(At(50, float.NegativeInfinity)));
            var e = tx.OpenMut(id);
            e.Set(FpSpatialUnit.Pos, At(60, 60));
            e.Set(FpSpatialUnit.Sv, new FpSv { Value = 9 });   // the archetype's other components take the same checked path, unrefused
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Open(id).Read(FpSpatialUnit.Pos).Bounds.MinX, Is.EqualTo(60f), "the refused values stored nothing; the legal one landed");
        }
    }

    [Test]
    [VerifiesRule("EP-03")]
    public void AClusterRef_UsedAfterItsEnumeratorMovedOn_Throws()
    {
        using var dbe = SetupEngine();
        SpawnUnit(dbe);
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.GetClusterEnumerator<FpUnit>();
        Assert.That(e.MoveNext(), Is.True);
        var stale = e.Current;
        Assert.That(stale.LiveCount, Is.EqualTo(1), "valid at its step");
        e.MoveNext();
        string message = null;
        try
        {
            _ = stale.OccupancyBits;
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
        }
        e.Dispose();
        Assert.That(message, Does.Contain("MoveNext"));
    }

#pragma warning restore TYPHON009
}
