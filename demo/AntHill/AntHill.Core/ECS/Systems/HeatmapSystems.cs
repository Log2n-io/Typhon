using System;

namespace AntHill.Core;

/// <summary>
/// Chunked-parallel max-reduce of one pheromone channel into the heatmap accumulator. Each chunk
/// owns a disjoint output-row range so there's no inter-chunk synchronization. Three instances
/// are registered (Food, Home, Fight); each writes its own accumulator resource so the auto-DAG
/// schedules them in parallel.
///
/// Per-cell algorithm: scan the 5 source rows that map to each output row, take the max of all
/// 5 source columns in the 5×5 block, write to the output cell. Net 25× downsample (1000×1000 →
/// 200×200) plus a max-pool reduction.
/// </summary>
internal sealed class PheroMaxReduceSystem : ChunkedCallbackSystem
{
    private const int ChunkCount = 16;       // 200 output rows / 16 ≈ 12-13 rows per chunk

    private readonly TyphonBridge _bridge;
    private readonly string _name;
    private readonly Func<float[]> _source;
    private readonly Func<float[]> _accum;
    private readonly string _writesResource;

    public PheroMaxReduceSystem(TyphonBridge bridge, string name, Func<float[]> source, Func<float[]> accum, string writesResource)
    {
        _bridge = bridge;
        _name = name;
        _source = source;
        _accum = accum;
        _writesResource = writesResource;
    }

    protected override void Configure(SystemBuilder b) => b
        .Name(_name)
        .Phase(AntPhases.Render)
        .ReadsResource("PheromoneGrid")
        .WritesResource(_writesResource)
        .ShouldRun(() => _bridge.HeatmapEnabled)
        .ChunkedParallel(ChunkCount);

    protected override void Execute(TickContext ctx)
    {
        var src = _source();
        var acc = _accum();
        const int gs = PheromoneGrid.GridSize;     // 1000
        const int hs = RenderFrame.HeatmapSize;    // 200

        // Partition output rows across chunks. Each chunk owns hStart..hEnd output rows and the
        // 5 source rows that feed each. Reset-then-fill so the previous tick's values don't bleed.
        var outRowsPerChunk = (hs + ctx.ChunkCount - 1) / ctx.ChunkCount;
        var hStart = ctx.ChunkIndex * outRowsPerChunk;
        var hEnd = Math.Min(hStart + outRowsPerChunk, hs);

        for (var hy = hStart; hy < hEnd; hy++)
        {
            var hiRow = hy * hs;

            // Reset this chunk's output rows (this-tick's values will go on top of zeros).
            for (var hx = 0; hx < hs; hx++) acc[hiRow + hx] = 0f;

            // Accumulate max from the 5 source rows that feed this output row.
            var srcRowStart = hy * 5;
            for (var sy = srcRowStart; sy < srcRowStart + 5; sy++)
            {
                var srcRow = sy * gs;
                for (var sx = 0; sx < gs; sx++)
                {
                    var hi = hiRow + sx / 5;
                    var si = srcRow + sx;
                    var v = src[si];
                    if (v > acc[hi]) acc[hi] = v;
                }
            }
        }
    }
}

/// <summary>
/// Packs the three downsampled accumulators (Food, Home, Fight) into the heatmap RGBA buffer.
/// Single-threaded — 40 K cells × ~5 ops = 0.05 ms, not worth chunking.
///
/// Reads the accumulators WITHOUT declaring a Reads dependency on them: the design accepts one
/// tick of tearing per channel in exchange for letting this run in parallel with the three
/// PheroMaxReduceSystems. The visual artifact is a single-frame slight mismatch on changed cells,
/// imperceptible against the 10 Hz pheromone decay timescale.
/// </summary>
internal sealed class HeatmapRgbaPackSystem : CallbackSystem
{
    private readonly TyphonBridge _bridge;
    public HeatmapRgbaPackSystem(TyphonBridge bridge) { _bridge = bridge; }

    protected override void Configure(SystemBuilder b) => b
        .Name("HeatmapRgbaPack")
        .Phase(AntPhases.Render)
        .WritesResource("Heatmap")
        .ShouldRun(() => _bridge.HeatmapEnabled);

    protected override void Execute(TickContext ctx)
    {
        var maxF = _bridge.HeatMaxFood;
        var maxH = _bridge.HeatMaxHome;
        var maxG = _bridge.HeatMaxFight;
        var rgba = _bridge.HeatmapRgba;
        var invMax = 255f / PheromoneGrid.MaxPheromone;
        var n = TyphonBridge.HeatmapPixels;

        for (var i = 0; i < n; i++)
        {
            var gv = (byte)Math.Min(maxF[i] * invMax, 255f);
            var bv = (byte)Math.Min(maxH[i] * invMax, 255f);
            var rv = (byte)Math.Min(maxG[i] * invMax, 255f);
            var p = i * 4;
            rgba[p + 0] = rv;       // R = Fight
            rgba[p + 1] = gv;       // G = Food
            rgba[p + 2] = bv;       // B = Home
            rgba[p + 3] = Math.Max(rv, Math.Max(gv, bv));
        }
    }
}
