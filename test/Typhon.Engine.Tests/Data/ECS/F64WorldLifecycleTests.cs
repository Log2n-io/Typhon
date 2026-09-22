using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The two halves of #919 that are neither a query nor a write: an f64 world must survive a <b>restart</b> (AC-7), and an <b>f32-tier archetype</b>
/// declared on a world it cannot address must fail at configuration rather than degrade silently (AC-9).
/// </summary>
/// <remarks>
/// <para>These belong together because they are the same claim from two directions. The engine's position on precision is that the tier is the
/// application's choice and the engine's job is to honour it exactly — so an f64 world must come back off disk bit-identically, and an f32 archetype must
/// not be allowed into a world where its own component cannot name a cell.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class F64WorldLifecycleTests : TestBase<F64WorldLifecycleTests>
{
    /// <summary>
    /// 2³⁶ plus a fractional offset — and the offset is the part that matters here.
    /// </summary>
    /// <remarks>
    /// A bare 2³⁶ is a power of two and therefore an EXACT f32, so a world based on it survives an f32 round trip of its origin unharmed and the reopen
    /// test below would pass against a build that had never been widened. The offset puts <c>WorldMin</c> between two adjacent floats 8 192 apart, so
    /// narrowing it anywhere on the reload path moves every cell origin by up to 4 096 — four cells — and the per-entity cell comparison catches it.
    /// </remarks>
    private const double FarOrigin = 68_719_476_736d + 12_345.678d;
    private const double CellSize = 1_000d;
    private const double WorldSpan = 64 * CellSize;

    private static SpatialGridConfig FarWorld => new(
        new Vector3D(FarOrigin, FarOrigin, FarOrigin),
        new Vector3D(FarOrigin + WorldSpan, FarOrigin + WorldSpan, FarOrigin + WorldSpan),
        CellSize);

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-7 — restart
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-7</c>: an f64 world's cell mapping and bounds are reconstructed on reopen, and the same query answers the same thing.
    /// </summary>
    /// <remarks>
    /// <para><b>Reopened WITHOUT calling <c>ConfigureSpatialGrid</c></b>, which is the demanding half. The grid config is persisted (#914 phase A widened
    /// that record from 8 ints to 16 and taught the loader to read both shapes), so a reopen has to reconstruct an f64 world frame from disk before
    /// <c>RebuildCellState</c> can place a single entity. If any of that narrowed, the entities would come back in the wrong cells — and the first
    /// assertion below, comparing cell keys entity by entity, is what would catch it. A query-count check alone would not: a whole population shifted by the
    /// same amount still answers a whole-world query correctly.</para>
    /// <para>The positions are chosen so the low 8 bits of each coordinate matter: at 2³⁶ an f32 round trip perturbs them by up to 4 096, which moves an
    /// entity four cells.</para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void F64World_ReopensWithoutBeingReconfigured_AndPlacesEveryEntityInTheSameCell()
    {
        var placed = new List<(long Id, int CellX, int CellY, int CellZ)>();
        var positions = new List<(double X, double Y, double Z)>();
        var rng = new Random(919);
        for (var i = 0; i < 200; i++)
        {
            positions.Add((
                FarOrigin + (rng.NextDouble() * 8d * CellSize),
                FarOrigin + (rng.NextDouble() * 8d * CellSize),
                FarOrigin + (rng.NextDouble() * 8d * CellSize)));
        }

        // Session 1 — create the f64 world and spawn into it.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<F64Pos3>();
            dbe.ConfigureSpatialGrid(FarWorld);
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                foreach (var p in positions)
                {
                    tx.Spawn<F64Unit3>(F64Unit3.Pos.Set(new F64Pos3
                    {
                        Bounds = new AABB3D { MinX = p.X, MinY = p.Y, MinZ = p.Z, MaxX = p.X, MaxY = p.Y, MaxZ = p.Z },
                    }));
                }
                tx.Commit();
            }
            dbe.WriteTickFence(1);

            placed = CellPlacements(dbe);
            Assert.That(placed, Has.Count.EqualTo(200), "every entity must be resident before the session closes");
        }

        // Session 2 — reopen WITHOUT ConfigureSpatialGrid, exactly as a generic tool does.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<F64Pos3>();

            Assert.DoesNotThrow(() => dbe.InitializeArchetypes(),
                "an f64 grid config must reconstruct from the persisted record without the game re-declaring it");

            var cfg = dbe.SpatialGrid.Config;
            Assert.Multiple(() =>
            {
                Assert.That(cfg.WorldMin, Is.EqualTo(new Vector3D(FarOrigin, FarOrigin, FarOrigin)), "the reconstructed world frame must be exact, not near");
                Assert.That(cfg.CellSize, Is.EqualTo(CellSize));
            });

            var reopened = CellPlacements(dbe);
            Assert.That(reopened, Has.Count.EqualTo(placed.Count), "the reopen lost or gained entities");

            var before = new Dictionary<long, (int, int, int)>();
            foreach (var e in placed)
            {
                before[e.Id] = (e.CellX, e.CellY, e.CellZ);
            }

            foreach (var e in reopened)
            {
                Assert.That(before.ContainsKey(e.Id), Is.True, $"entity {e.Id:X} appeared only after the reopen");
                Assert.That((e.CellX, e.CellY, e.CellZ), Is.EqualTo(before[e.Id]),
                    $"entity {e.Id:X} came back in a different cell — the rebuild narrowed the world frame somewhere");
            }
        }
    }

    /// <summary>Every live entity with the cell coordinates of the cluster it is resident in.</summary>
    private static unsafe List<(long Id, int CellX, int CellY, int CellZ)> CellPlacements(DatabaseEngine dbe)
    {
        var outp = new List<(long, int, int, int)>();
        var cs = dbe._archetypeStates[Archetype<F64Unit3>.Metadata.ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var cellKey = cs.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }
                var (cx, cy, cz) = dbe.SpatialGrid.CellKeyToCoords(cellKey);
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    outp.Add((*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8), cx, cy, cz));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return outp;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-9 — an f32 archetype may not be declared on a world it cannot address
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An f32-tier archetype on a 2³⁶ world fails at <c>InitializeArchetypes</c>, naming the f64 tiers as the fix.</summary>
    /// <remarks>
    /// It fails at STARTUP rather than at the first spawn because that is the last moment the developer can still act on it, and because the failure it
    /// prevents is silent: the archetype's own f32 component cannot name the cell its entity is in, so entities in different cells are filed together and
    /// every query answers plausibly and wrongly. Nothing inside the engine can detect that later — the precision was lost before the engine saw the value.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void F32Archetype_OnAWorldF32CannotAddress_FailsAtStartup()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<W3DPos>();          // AABB3F — an f32 tier
        dbe.ConfigureSpatialGrid(FarWorld);

        var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Does.Contain("AABB3F"), "the message must name the offending field type");
            Assert.That(ex.Message, Does.Contain("AABB3D"), "and must point at the f64 tier as the fix, which is the whole reason #914/#919 built it");
        });
    }

    /// <summary>The same world accepts an f64-tier archetype — the check is about the field's precision, not about the world being large.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void F64Archetype_OnTheSameWorld_IsAccepted()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<F64Pos3>();
        dbe.ConfigureSpatialGrid(FarWorld);

        Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
    }

    /// <summary>An ordinary small world still accepts an f32 archetype — the guard must not be a tax on the case everything uses.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void F32Archetype_OnAnOrdinaryWorld_IsAccepted()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<W3DPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector3D(0, 0, 0), new Vector3D(1_000, 1_000, 1_000), 100d));

        Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
    }

    /// <summary>A small world with fractional bounds is accepted — the guard tests resolution, not exact representability.</summary>
    /// <remarks>
    /// The regression guard for a real over-restriction this check had when it was first written. Implementing #919 AC-9's literal wording — "every cell
    /// origin is exactly representable in f32" — rejected this world, which has no exactly-representable origin at all and yet resolves ~10⁻⁸ where a cell
    /// is 100 units wide. Failing startup for a world that works is a worse bug than the one the guard exists to prevent.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void F32Archetype_OnASmallWorldWithFractionalBounds_IsAccepted()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<W3DPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(new Vector3D(0.1d, 0.1d, 0.1d), new Vector3D(1_000.1d, 1_000.1d, 1_000.1d), 100d));

        Assert.DoesNotThrow(() => dbe.InitializeArchetypes(),
            "0.1 is not an exact f32, but an f32 step here is ~1e-8 against a 100-unit cell — every entity lands in the right cell");
    }

    /// <summary>
    /// The criterion agrees with the property it stands for: can two points one cell apart, at the axis's extreme, be told apart in f32?
    /// </summary>
    /// <remarks>
    /// <c>AxisIsResolvableInF32</c> answers that with one ULP computation. The loop below answers it by construction — narrow both points and compare — and
    /// the two must agree on worlds that pass AND on worlds that fail, or the guard has its own silent failure mode. Testing the property rather than the
    /// arithmetic is what caught the first version of this check, which asked whether every origin was exactly representable and so rejected the fractional
    /// small world above.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void TheResolutionCriterion_AgreesWithWhetherF32CanSeparateAdjacentCells()
    {
        (double Min, double Max, double Cell, string What)[] worlds =
        [
            (0d, 1_000d, 100d, "the ordinary case"),
            (0d, 64_000d, 1_000d, "a larger ordinary world"),
            (-5_000d, 5_000d, 250d, "straddling zero"),
            (0.1d, 1_000.1d, 100d, "fractional bounds, small world"),
            (16_777_216d, 16_777_216d + 4_096d, 256d, "2^24, cell above the step"),
            (1_048_576d, 1_048_576d + 10_000d, 0.5d, "2^20 with sub-unit cells — the step is 0.125, still under the cell"),
            (1_073_741_824d, 1_073_741_824d + 10_000d, 64d, "2^30 with a 64-unit cell — the step is 128, WIDER"),
            (FarOrigin, FarOrigin + WorldSpan, CellSize, "2^36 — the case #919 exists for"),
        ];

        foreach (var w in worlds)
        {
            var criterion = SpatialGrid.AxisIsResolvableInF32(w.Min, w.Max, w.Cell, out var step);

            // The property, quantified over POSITION rather than sampled at one point. Two points a cell apart can survive narrowing at one offset and
            // collapse at another — 2^30 with 64-unit cells does both — so "is this world resolvable" has to mean "is there NO pair that collapses".
            // Sampling a single offset is how the first version of this test disagreed with a criterion that was right.
            var extreme = (float)Math.Max(Math.Abs(w.Min), Math.Abs(w.Max));
            var probeStep = (MathF.BitIncrement(extreme) - extreme) / 64f;   // derived here, not read from the implementation under test
            var anyPairCollapses = false;
            for (var i = 0; i < 64 && !anyPairCollapses; i++)
            {
                var probe = extreme + (i * probeStep);
                anyPairCollapses = (float)probe == (float)(probe + w.Cell);
            }

            Assert.That(criterion, Is.EqualTo(!anyPairCollapses),
                $"{w.What}: criterion says resolvable={criterion} (step {step} vs cell {w.Cell}) but a collapsing pair {(anyPairCollapses ? "exists" : "does not exist")}");
        }
    }
}
