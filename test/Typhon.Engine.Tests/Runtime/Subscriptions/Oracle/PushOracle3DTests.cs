using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The deep implementation against the truth (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 10, 1.5.2): the 3D oracle, the radius
/// shell, and the degeneracy of a flat world.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushOracle3DTests : TestBase<PushOracle3DTests>
{
    /// <summary>
    /// A 2 km cube at c = 64 m (32³ cells, windows of 11³): 3D flyers that drift, climb and teleport across clusters, 2D walkers on the plane z = 0 in the
    /// same world, statics; sessions that walk and climb in 3D and teleport, at skip rates from 0 to 90 %. Every quiet point, every replica is its sphere.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AClientsWorldIsItsSphereInADeepGrid()
    {
        using var oracle = VolumeOracle.Create(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), volumetric: true, seed: 3301,
            [0, 30, 60, 90], nameof(PushOracle3DTests), radius: 192, cellM: 64);
        Assert.That(oracle.Push.Deep, Is.True, "a volumetric world is served by the deep implementation");

        long compared = 0, required = 0;
        for (var point = 0; point < 5; point++)
        {
            for (var i = 0; i < 40; i++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"deep grid, point {point}");
            compared += oracle.ComparedAtLastPoint;
            required += oracle.RequiredAtLastPoint;
        }

        Assert.Multiple(() =>
        {
            Assert.That(required, Is.GreaterThan(50), "the spheres held too little for the comparison to prove anything");
            Assert.That(compared, Is.GreaterThanOrEqualTo(required));
            Assert.That(oracle.Teleports, Is.GreaterThan(10), "no flyer changed cluster by teleport");
            Assert.That(oracle.Climbs, Is.GreaterThan(10), "no flyer climbed");
            Assert.That(oracle.Destroyed, Is.GreaterThan(5), "nothing was destroyed");
            Assert.That(oracle.ViewpointTeleports, Is.GreaterThan(2), "no session teleported");
            Assert.That(oracle.Push.ShadowIllegal, Is.Zero);
            Assert.That(oracle.Push.VerifyOccupancy(), Is.Zero, "the occupancy disagrees with a recount");
        });
    }

    /// <summary>
    /// 10 § 5's shell: a session's radius goes 192 → 1 500 → 192 m through the seam while it walks. Each change sweeps the shell between the two spheres —
    /// enters on the way out, leaves on the way in — and never resets. The window is sized for 1 500 m, the largest radius: ⌈R / c⌉ ≤ 4 deep.
    /// </summary>
    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    public void TheRadiusShellSweepsWithoutAResetInADeepGrid()
    {
        using var oracle = VolumeOracle.Create(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), volumetric: true, seed: 3302, [0, 0],
            nameof(PushOracle3DTests), radius: 1500, cellM: 375, flyers: 400, walkers: 200);
        Shell(oracle);
    }

    /// <summary>The same shell in a flat grid: ⌈R / c⌉ ≤ 5.</summary>
    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    public void TheRadiusShellSweepsWithoutAResetInAFlatGrid()
    {
        using var oracle = VolumeOracle.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), volumetric: false, seed: 3302, [0, 0],
            nameof(PushOracle3DTests), radius: 1500, cellM: 300, flyers: 600, walkers: 300, rocks: 120, spanM: 2500);
        Shell(oracle);
    }

    private static void Shell(VolumeOracle oracle)
    {
        foreach (var radius in new[] { 192.0, 1500.0, 192.0 })
        {
            for (var s = 0; s < oracle.Sessions.Length; s++)
            {
                oracle.SetRadius(s, radius);
            }

            var resets = oracle.Push.Resets;
            for (var i = 0; i < 20; i++)
            {
                oracle.Step(teleports: false);
            }

            oracle.Quiesce();
            oracle.AssertConverged($"radius {radius}");
            Assert.That(oracle.RequiredAtLastPoint, Is.GreaterThan(0), $"radius {radius}: the spheres held nothing");
            Assert.That(oracle.Push.Resets, Is.EqualTo(resets), $"radius {radius}: a radius change reset a session");
        }
    }

    /// <summary>
    /// 10 § 3.4's degeneracy: in a flat grid a flyer (<c>pos3</c>, at an altitude the flat world spans) and a walker (<c>pos2</c>) spawned at the same
    /// plane coordinates and moved by the same plane steps are held by exactly the same sessions at every quiet point — the third axis changes nothing
    /// where the grid has none. For both implementations.
    /// </summary>
    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [VerifiesRule("SUB-16")]
    public void AThirdAxisChangesNothingInAFlatGrid() => Degenerate(deepImplementation: false);

    /// <summary>The same, served by the deep implementation forced onto the flat grid.</summary>
    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [VerifiesRule("SUB-16")]
    public void AThirdAxisChangesNothingInAFlatGridServedDeep() => Degenerate(deepImplementation: true);

    private void Degenerate(bool deepImplementation)
    {
        using var oracle = VolumeOracle.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), volumetric: false, seed: 3303, [0, 50, 0],
            nameof(PushOracle3DTests), radius: 400, cellM: 150, forceDeep: deepImplementation, flyers: 500, walkers: 500, spanM: 1500);
        Assert.That(oracle.Push.Deep, Is.EqualTo(deepImplementation));
        var held = 0;
        for (var point = 0; point < 4; point++)
        {
            for (var i = 0; i < 30; i++)
            {
                oracle.Step();
            }

            oracle.Quiesce();
            oracle.AssertConverged($"degeneracy point {point}");
            var sets = oracle.HeldMoversBySpawnOrder();
            for (var s = 0; s < sets.Count; s++)
            {
                Assert.That(sets[s].Flyers, Is.EqualTo(sets[s].Walkers), $"point {point}, session {s}: pos3 and pos2 movers held differently");
                held += sets[s].Walkers.Count;
            }
        }

        Assert.That(held, Is.GreaterThan(40), "the sessions held too few movers for the comparison to prove anything");
    }
}
