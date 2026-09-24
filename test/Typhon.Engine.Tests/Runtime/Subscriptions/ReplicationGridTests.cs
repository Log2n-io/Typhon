using NUnit.Framework;
using System;
using System.Numerics;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The replication grid (design/Subscriptions/10 § 3): its cell side is declared, never derived, and everything else follows from it and the spatial world.
/// </summary>
[TestFixture]
[NonParallelizable]
class ReplicationGridTests : TestBase<ReplicationGridTests>
{
    private static SpatialGridConfig Flat16Km() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(16_000, 16_000), 256);

    [Test]
    public void TheSwgGeometryResolvesToTheElevenCellWindow()
    {
        var grid = ReplicationGrid.Resolve(64, Flat16Km(), 192);

        Assert.Multiple(() =>
        {
            Assert.That(grid.CellM, Is.EqualTo(64));
            Assert.That((grid.DimX, grid.DimY, grid.DimZ), Is.EqualTo((250, 250, 1)));
            Assert.That((grid.OriginX, grid.OriginY), Is.EqualTo((0d, 0d)));
            Assert.That((grid.Half, grid.Window), Is.EqualTo((5, 11)));
            Assert.That(grid.AnchorSlack, Is.EqualTo(4d));
            Assert.That(grid.Flat, Is.True);
        });
    }

    [Test]
    public void AFlatSpatialWorldGivesAGridOneCellDeepWhateverTheCellSide()
    {
        // The flat world's Z extent is one spatial cell (256 m); a 16 m replication cell would otherwise make it 17 cells deep and give it an altitude.
        var grid = ReplicationGrid.Resolve(16, Flat16Km(), 48);

        Assert.That(grid.DimZ, Is.EqualTo(1));
    }

    [Test]
    public void AVolumetricSpatialWorldGivesADeepGrid()
    {
        var spatial = new SpatialGridConfig(new Vector3D(-1024, -1024, -1024), new Vector3D(1024, 1024, 1024), 128);

        var grid = ReplicationGrid.Resolve(64, spatial, 192);

        Assert.That((grid.DimX, grid.DimY, grid.DimZ, grid.Flat), Is.EqualTo((32, 32, 32, false)));
    }

    [Test]
    [VerifiesRule("SUB-16")]
    public void AnUndeclaredCellSideIsRefused([Values(0, 1, 2, 3)] int which)
    {
        var cellM = which switch { 0 => 0d, 1 => -1d, 2 => double.NaN, _ => double.PositiveInfinity };
        var ex = Assert.Throws<InvalidOperationException>(() => ReplicationGrid.Resolve(cellM, Flat16Km(), 192));

        Assert.That(ex!.Message, Does.Contain("ReplicationCellM"));
    }

    [Test]
    [VerifiesRule("SUB-16")]
    public void AGridWiderThanTheCellKeyIsRefused()
    {
        // 16 km at 5 mm is 3.2 M cells per axis, past the 2²¹ a key addresses.
        var ex = Assert.Throws<InvalidOperationException>(() => ReplicationGrid.Resolve(0.005, Flat16Km(), 0));

        Assert.That(ex!.Message, Does.Contain("ReplicationCellM").And.Contain("2097152"));
    }

    [Test]
    [VerifiesRule("SUB-16")]
    public void AWindowPastSixteenCellsIsRefusedAndTheMessageNamesTheSmallestSide()
    {
        // ⌈192 / 30⌉ = 7: a window of 19 cells.
        var ex = Assert.Throws<InvalidOperationException>(() => ReplicationGrid.Resolve(30, Flat16Km(), 192));

        Assert.That(ex!.Message, Does.Contain("ReplicationCellM").And.Contain("19 cells").And.Contain("38.4"));
        Assert.That(ReplicationGrid.Resolve(38.4, Flat16Km(), 192).Window, Is.EqualTo(15), "the side the message names is accepted");
    }

    [Test]
    public void AWorldOnlyGridHasTheSmallestWindowAndNoSlack()
    {
        var grid = ReplicationGrid.Resolve(64, Flat16Km(), 0);

        Assert.That((grid.Radius, grid.Half, grid.Window, grid.AnchorSlack), Is.EqualTo((0d, 2, 5, 0d)));
    }

    [Test]
    [VerifiesRule("SUB-16")]
    public void ARuntimeThatObservesAnArchetypeWithoutACellSideRefusesToStart([Values] bool world)
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var harness = ReplicationHarness.Create(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile("p", p => (world ? p.World() : p.Sphere(192)).Of<ProjCreature>());
            }, "GridRefusal", new SubscriptionsOptions { MaxSessions = 16 });
        });

        Assert.That(ex!.Message, Does.Contain("ReplicationCellM"));
    }
}
