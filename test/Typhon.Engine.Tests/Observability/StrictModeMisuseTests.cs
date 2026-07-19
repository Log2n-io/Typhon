using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Integration tests for strict mode (#422): with <see cref="CheckConfig.Enabled"/> on, user-facing API misuse throws
/// <see cref="InvalidOperationException"/> instead of silently proceeding. These exercise the real converted call sites
/// (<see cref="EntityRef"/>, thread-affinity, destroy-null) rather than the primitive in isolation.
///
/// <para>
/// Each test <see cref="Assume"/>s strict mode is enabled; the suite turns it on via <c>typhon.telemetry.json</c>
/// (<c>Checks:Enabled=true</c>). If a run has it off, these are Inconclusive rather than failing — the gate is a
/// <c>static readonly</c> baked at process load and cannot be toggled per-test. The "off → no-op" direction is covered at
/// the primitive level in <see cref="CheckConfigTests"/>.
/// </para>
/// </summary>
[NonParallelizable]
class StrictModeMisuseTests : TestBase<StrictModeMisuseTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private EntityId SpawnOne(DatabaseEngine dbe)
    {
        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(1);
        var id = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
        t.Commit();
        return id;
    }

    [Test]
    public void Write_ThroughReadOnlyRef_Throws()
    {
        Assume.That(CheckConfig.Enabled, Is.True, "Requires strict mode (typhon.telemetry.json Checks:Enabled=true).");
        using var dbe = SetupEngine();
        var id = SpawnOne(dbe);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var e = tx.Open(id);                     // read-only ref (Open, not OpenMut)
            e.Write(CompAArch.A) = new CompA(2);     // writing through a read-only ref is misuse → strict-mode throw
        });
    }

    [Test]
    public void Destroy_NullEntity_Throws()
    {
        Assume.That(CheckConfig.Enabled, Is.True, "Requires strict mode (typhon.telemetry.json Checks:Enabled=true).");
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => t.Destroy(EntityId.Null));
    }

    [Test]
    public void Transaction_UsedFromWrongThread_Throws()
    {
        Assume.That(CheckConfig.Enabled, Is.True, "Requires strict mode (typhon.telemetry.json Checks:Enabled=true).");
        using var dbe = SetupEngine();
        var id = SpawnOne(dbe);

        using var tx = dbe.CreateQuickTransaction();   // owning thread = this test thread

        Exception caught = null;
        var worker = new Thread(() =>
        {
            try
            {
                tx.Open(id);   // Open() calls AssertThreadAffinity() — a different thread than the creator → throw
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        worker.Start();
        worker.Join();

        Assert.That(caught, Is.TypeOf<InvalidOperationException>(),
            "Using a transaction from a thread other than its creator must throw under strict mode.");
    }
}
