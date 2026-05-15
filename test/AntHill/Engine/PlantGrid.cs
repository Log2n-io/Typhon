using System;
using System.Collections.Generic;

namespace AntHill;

/// <summary>
/// Phase 6C — 100 k plant billboards spread across the world as plain arrays. Lives outside ECS
/// for the same reasons as <see cref="FireGrid"/> and <c>PheromoneGrid</c>: per-plant state with
/// no MVCC / spatial-query / parallelism payoff — just a flat sweep every CA tick.
///
/// Spawn distribution is stratified: each plant lives in one of <see cref="StratumPerSide"/>²
/// lattice cells (≈63 sim units / 0.32 m apart) with intra-cell jitter. Cheaper than Poisson-disc,
/// visually indistinguishable at this density. Kind (grass / moss / lichen / leaf) is uniform random.
///
/// Plants couple back into the fire CA via <see cref="DensityFactor"/>: <c>FireGrid.Tick</c> reads
/// this span and rescales per-cell spread probability in [0.3×, 1.0×] of base. Burning a plant
/// decrements its cell's <see cref="Density"/> and the renderer pushes the dirty index for a
/// charred-tint update next Godot frame.
/// </summary>
public sealed class PlantGrid
{
    public const int StratumPerSide = 316;                                  // chosen so StratumPerSide² ≈ 100 k → 99 856 plants
    public const int Count = StratumPerSide * StratumPerSide;               // exact array size (no trailing default-zero entries, which would alias to Kind=0)
    public const float StratumSizeSim = TyphonBridge.WorldSize / StratumPerSide;  // ≈ 63 sim units = 0.32 m
    public const int DensityNorm = 1;                                       // any plant on the cell = full spread bonus (re-tuned when FireGrid 200→400 quartered the per-cell plant count: avg 100k/160k ≈ 0.6 plants/cell)
    public const byte BurnCountdownStart = 20;                              // 20 CA ticks × 0.1 s = ~2 s charred-then-despawn
    public const byte Alive = 0;                                            // State[i] = 0 → alive; 1..BurnCountdownStart → countdown; 255 → despawned
    public const byte Despawned = 255;
    public const int KindCount = 4;                                         // grass / moss / lichen / leaf

    // Per-plant arrays — SOA, all length Count.
    public float[] X { get; }                                               // sim units
    public float[] Z { get; }                                               // sim units
    public float[] Y { get; }                                               // world metres, baked from heightmap at spawn (never mutated)
    public byte[]  Kind { get; }                                            // 0..KindCount-1
    public byte[]  State { get; }
    public int[]   Cell { get; }                                            // FireGrid cell index — cached at spawn so the tick loop avoids re-clamping per plant

    // Per-cell aggregates — length FireGrid.CellCount.
    public int[]   Density { get; }                                         // live count of Alive plants in each cell (decrements on charring)
    public byte[]  DensityFactor { get; }                                   // 0..255 ≈ clamp(Density / DensityNorm) × 255 — handed to FireGrid.Tick

    // Plant count per kind (for sizing 4 MultiMeshInstance3D buffers without re-counting at render).
    public int[]   CountByKind { get; } = new int[KindCount];

    // Indices the next render frame must re-colour (plants whose State changed since the last drain).
    // Soft-bounded by per-CA-tick fire spread × ~10 plants/cell worst case; overflow is benign because
    // the next CA tick will re-enqueue any missed transitions on the same cell.
    public Queue<int> DirtyIndices { get; } = new(4096);

    // LCG RNG — deterministic, no allocations, no shared state. Distinct seed from FireGrid so the
    // spawn pattern doesn't correlate with lightning strikes.
    private uint _rng = 0xBEEFFACEu;

    /// <summary>
    /// Stratified-jitter spawn over the whole world. <paramref name="sampleHeightWorld"/> takes
    /// world-metre coords (x, z) and returns the terrain Y in metres — wired from
    /// <see cref="HeightmapResource.Sample"/> via <c>TyphonBridge.SetHeightmap</c>.
    /// </summary>
    public PlantGrid(Func<float, float, float> sampleHeightWorld)
    {
        ArgumentNullException.ThrowIfNull(sampleHeightWorld);

        X = new float[Count];
        Z = new float[Count];
        Y = new float[Count];
        Kind = new byte[Count];
        State = new byte[Count];
        Cell = new int[Count];
        Density = new int[FireGrid.CellCount];
        DensityFactor = new byte[FireGrid.CellCount];

        const float SimToWorld = 100f / TyphonBridge.WorldSize;             // mirrors AntRenderer.SimToWorld (= 0.005)

        int written = 0;
        for (var sy = 0; sy < StratumPerSide; sy++)
        {
            for (var sx = 0; sx < StratumPerSide; sx++)
            {
                var jx = NextRand();
                var jz = NextRand();
                var simX = (sx + jx) * StratumSizeSim;
                var simZ = (sy + jz) * StratumSizeSim;
                var worldX = simX * SimToWorld;
                var worldZ = simZ * SimToWorld;

                X[written] = simX;
                Z[written] = simZ;
                Y[written] = sampleHeightWorld(worldX, worldZ);
                var k = (byte)(NextRand() * KindCount);
                if (k >= KindCount) k = KindCount - 1;                      // guard the 1.0 edge case (NextRand returns [0,1))
                Kind[written] = k;
                CountByKind[k]++;
                State[written] = Alive;

                var cx = (int)(simX * FireGrid.InvCellSizeSim);
                var cz = (int)(simZ * FireGrid.InvCellSizeSim);
                if (cx < 0) cx = 0; else if (cx >= FireGrid.Size) cx = FireGrid.Size - 1;
                if (cz < 0) cz = 0; else if (cz >= FireGrid.Size) cz = FireGrid.Size - 1;
                var c = cz * FireGrid.Size + cx;
                Cell[written] = c;
                Density[c]++;
                written++;
            }
        }

        RecomputeDensityFactor();
    }

    /// <summary>
    /// Recompute the 0..255 density factor from the raw per-cell counts. Called once at spawn and
    /// again whenever a plant transitions out of Alive (i.e. <see cref="VegetationSystem"/> after
    /// a CA-tick sweep that charred at least one plant).
    /// </summary>
    public void RecomputeDensityFactor()
    {
        for (var c = 0; c < FireGrid.CellCount; c++)
        {
            var d = Density[c];
            var f = d >= DensityNorm ? 1f : d * (1f / DensityNorm);
            DensityFactor[c] = (byte)(f * 255f);
        }
    }

    private float NextRand()
    {
        _rng = _rng * 1664525u + 1013904223u;
        return (_rng >> 8) * (1f / 16777216f);                              // top 24 bits → [0, 1)
    }
}
