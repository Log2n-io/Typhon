using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #897 / ENG-18: naming a component with no <c>[SpatialIndex]</c> in a fluent spatial predicate is refused, and refused
/// with strict mode OFF — which is the default everywhere and what the Release NuGet ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> All four predicates guarded the condition behind <c>CheckConfig.Require(CheckConfig.Enabled, …)</c>,
/// and <c>CheckConfig.Enabled</c> is a <c>static readonly bool</c> that is off by default so the JIT eliminates the check
/// entirely. On every default deployment the guard was compiled away and the query dereferenced a null index state, so the
/// user got a bare <see cref="NullReferenceException"/> from inside the engine for ordinary API misuse — which reads as an
/// engine bug and gets filed as one, instead of prompting the attribute that was missing.
/// </para>
/// <para>
/// <b>Why the strict-mode gate was the wrong home for this one.</b> The gate's bargain is "a diagnostic when on, silence when
/// off". Here the alternative was not silence but a misleading crash, and that is the distinguishing test #897 states. The
/// typed surface, <c>ClusterSpatialQuery&lt;TArch&gt;</c>, always threw and named the fix; this brings the fluent surface to
/// the same behaviour.
/// </para>
/// <para>
/// <b>Evidence that it bit a real consumer:</b> <c>QuerySpecCompilerSpatialTests</c> in the Workbench suite carries a case
/// whose comment reads "must be rejected before the engine NREs in Release" — a downstream component built its own guard
/// because this one was not there in the configuration that ships.
/// </para>
/// <para>
/// The components come from <c>ClusterStorageTests</c>' schema: <c>ClPosition</c> is a plain SingleVersion struct with no
/// <c>[SpatialIndex]</c>, which is exactly the shape a user who forgot the attribute has.
/// </para>
/// </remarks>
/// <remarks>
/// <b><c>[Category("ChecksOffGated")]</c> is what makes these cases evidence rather than decoration.</b> This suite's
/// <c>typhon.telemetry.json</c> sets <c>Typhon:Checks:Enabled</c> true for every fixture, so a case asserting the throw
/// would pass against the gated guard too and prove nothing. The gate runs this category in a forked process with
/// <c>TYPHON__CHECKS__ENABLED=false</c> — the shipped configuration. Verified by mutation: restoring the
/// <c>CheckConfig.Enabled &amp;&amp;</c> prefix turns all five red under that environment and green without it.
/// </remarks>
[NonParallelizable]
[Category("ChecksOffGated")]
class FluentSpatialQueryMisuseTests : TestBase<FluentSpatialQueryMisuseTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClPosition>();
        dbe.RegisterComponentFromAccessor<ClMovement>();
        dbe.RegisterComponentFromAccessor<ClVHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The assertion every case below shares: the right exception, naming the component and both halves of the fix.</summary>
    private static void AssertRefused(Action predicate)
    {
        var ex = Assert.Throws<InvalidOperationException>(predicate);
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain(nameof(ClPosition)), "the message must name the component the user wrote");
            Assert.That(ex.Message, Does.Contain("[SpatialIndex]"), "and the attribute that is missing");
            Assert.That(ex.Message, Does.Contain("ConfigureSpatialGrid"), "and the other half of the fix, as the typed surface does");
        });
    }

    [Test]
    public void WhereNearby_OnAComponentWithNoSpatialIndex_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False, "the point of the case is that the guard holds with strict mode off");
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        AssertRefused(() => tx.Query<ClAnt>().WhereNearby<ClPosition>(0, 0, 0, 10));
    }

    [Test]
    public void WhereInAABB_OnAComponentWithNoSpatialIndex_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        AssertRefused(() => tx.Query<ClAnt>().WhereInAABB<ClPosition>(0, 0, 0, 10, 10, 10));
    }

    [Test]
    public void WhereRay_OnAComponentWithNoSpatialIndex_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        AssertRefused(() => tx.Query<ClAnt>().WhereRay<ClPosition>(0, 0, 0, 1, 0, 0, 100));
    }

    [Test]
    public void WhereFrustum_OnAComponentWithNoSpatialIndex_IsRefused()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        // Four planes as (nx, ny, nz, d) quads — the box 0..100 in x and y, which is a valid frustum, so the refusal is
        // about the missing attribute and nothing else.
        double[] planes =
        [
            1, 0, 0, 0,
            -1, 0, 0, 100,
            0, 1, 0, 0,
            0, -1, 0, 100,
        ];
        AssertRefused(() => tx.Query<ClAnt>().WhereFrustum<ClPosition>(planes, 4, 0, 0, 0, 100, 100, 100));
    }

    /// <summary>
    /// The refusal is a diagnostic, not a latch: it must not leave the transaction unusable, or a tool that catches it and
    /// carries on (the Query Console does exactly this) would be broken by the fix that was supposed to help it.
    /// </summary>
    [Test]
    public void TheRefusalLeavesTheTransactionUsable()
    {
        Assume.That(CheckConfig.Enabled, Is.False);
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClPosition(1, 2);
        var mov = new ClMovement(0, 0);
        var id = tx.Spawn<ClAnt>(ClAnt.Position.Set(in pos), ClAnt.Movement.Set(in mov));
        tx.Commit();

        using var qTx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => qTx.Query<ClAnt>().WhereInAABB<ClPosition>(0, 0, 0, 10, 10, 10));

        var all = qTx.Query<ClAnt>().Execute();
        Assert.That(all, Does.Contain(id), "the same transaction still answers a non-spatial query");
    }
}
