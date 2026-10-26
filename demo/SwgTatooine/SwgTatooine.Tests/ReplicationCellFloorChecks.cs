using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// A world scaled down by <c>--world</c> can still be served: the replication cell is floored so a player's sphere stays inside the engine's window bound.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this exists to keep out.</b> <c>SimConfig.ResolveCellSize</c> scales the spatial cell with the world (256 m x <c>ContentScale</c>) and
/// <see cref="TatooineReplication.PlayerRadiusM"/> does not scale, so below a certain world size the two are incompatible: a sphere session's window is
/// <c>2(ceil(R / c) + 2) + 1</c> cells per axis against <c>ReplicationGrid.MaxWindow</c> of 16, giving <c>ceil(R / c) &lt;= 5</c> and a floor of
/// <c>R / 5</c> = 38.4 m for a 192 m sphere. At <c>--world 2</c> the scaled cell is 31.25 m, and <c>StartReplication</c> refused the player profile
/// outright — <c>--world 2 --pop 16</c> crashed on the spot.
/// </para>
/// <para>
/// <b>Asserted on a real <c>StartReplication</c> rather than on the arithmetic</b>, because the arithmetic is the part that was already right: both
/// constants were correct on their own and the refusal came from declaring the profiles. A test that recomputed the bound would have agreed with the
/// broken code.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class ReplicationCellFloorChecks
{
    /// <summary>The world edge, in km, whose scaled spatial cell lands under the bound: 2 km gives 31.25 m against the 38.4 m a 192 m sphere needs.</summary>
    private const float UnservableWorldEdgeKm = 2f;

    private string _dir;
    private TatooineSim _sim;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
    }

    [TearDown]
    public void TearDown()
    {
        _sim?.Dispose();
        _sim = null;
        TatooineReplication.ResetSessionAccounting();
        Worlds.Delete(_dir);
    }

    /// <summary>A world small enough to scale the spatial cell under the window bound still starts replication.</summary>
    [Test]
    [Category("Integration")]
    public void AWorldScaledBelowTheWindowBoundStillStartsReplication()
    {
        var config = Worlds.Small(_dir);
        config.WorldEdgeKm = UnservableWorldEdgeKm;
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;

        // The premise: without the floor this configuration's spatial cell cannot carry a player's sphere. If this ever stops holding — a smaller radius, a
        // larger bound — the case below passes for a reason that has nothing to do with the floor, so it is asserted rather than assumed.
        var spatialCell = config.ResolveCellSize();
        Assert.That(
            spatialCell,
            Is.LessThan(TatooineReplication.PlayerRadiusM / 5d),
            $"the premise: a {UnservableWorldEdgeKm:F0} km world's spatial cell must be too small for a {TatooineReplication.PlayerRadiusM:F0} m sphere");

        _sim = new TatooineSim(config);
        _sim.Initialize();
        Assert.That(
            TatooineReplication.ReplicationCellM,
            Is.GreaterThanOrEqualTo(TatooineReplication.PlayerRadiusM / 5d),
            "the replication cell must be floored above R / 5, or the player profile cannot be declared");
        Assert.That(TatooineReplication.ReplicationCellM, Is.GreaterThan(spatialCell), "the floor must have bound, or this world is not the small one");

        // The assertion that matters: this is the call that threw.
        Assert.DoesNotThrow(() => _sim.StartReplication(), "a scaled-down world must still be able to declare its profiles");
    }

    /// <summary>The floor does not bind at the planet's real size, so nothing measured on the default world moves.</summary>
    [Test]
    public void TheFloorDoesNotBindAtTheRealWorldSize()
    {
        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;

        var spatialCell = config.ResolveCellSize();
        _sim = new TatooineSim(config);
        _sim.Initialize();

        Assert.That(
            TatooineReplication.ReplicationCellM,
            Is.EqualTo((double)spatialCell),
            "at the real world size the replication cell must still follow the spatial one 1:1, or every measurement taken on it moved");
    }
}
