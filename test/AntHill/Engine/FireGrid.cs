using System;

namespace AntHill;

/// <summary>
/// Phase 6B — Drossel-Schwabl forest fire cellular automaton. Lives outside ECS by design
/// (per-cell state of a dense 200×200 grid has none of the MVCC / spatial-query / parallelism
/// payoff the ECS path is built for; it's a flat sweep). Tick is run at 10 Hz by
/// <see cref="FireTickSystem"/> in <see cref="AntPhases.Trail"/>.
///
/// Cell states (1 byte each):
///   <list type="bullet">
///     <item><c>0 = Empty</c> — burnt-out ash, slowly regrows to Fuel.</item>
///     <item><c>1 = Fuel</c> — flammable.</item>
///     <item><c>2 = Burning</c> — propagates to neighbours next tick, then becomes Empty.</item>
///   </list>
///
/// World-to-cell mapping mirrors <c>PheromoneGrid</c>: cell size in sim units is
/// <see cref="TyphonBridge.WorldSize"/> / <see cref="Size"/>, so each cell covers 100 sim units
/// (= 0.5 m worldspace at AntHill's 20 000:100 scale).
/// </summary>
public sealed class FireGrid
{
    public const int Size = 400;                            // 400×400 = 160 k cells (was 200×200 = 40 k)
    public const int CellCount = Size * Size;
    public const float CellSizeSim = TyphonBridge.WorldSize / Size;  // 50 sim units = 0.25 m per cell
    public const float InvCellSizeSim = Size / TyphonBridge.WorldSize;

    public const byte Empty = 0;
    public const byte Fuel  = 1;
    // Burning is a countdown state: a cell ignites at BurnStart and decrements each CA tick.
    // When it reaches BurnMin and ticks again, it becomes Empty (ash). With CA at 10 Hz and
    // BurnStart=10, each cell stays Burning for ~1 sim-second. Any state in [BurnMin, BurnStart]
    // counts as burning for neighbour-spread and shader purposes.
    public const byte BurnMin   = 2;
    public const byte BurnStart = 21;   // 20 stages × 0.1 s/tick ≈ 2.0 s per-cell burn duration

    // Tunables, tuned for self-extinguishing fires:
    //   SpreadPerNeighbour = 0.025 / tick × 10 burn ticks = 22% cumulative per neighbour.
    //     With 4 neighbours per cell, expected children-per-burning-cell ≈ 0.88. Just subcritical,
    //     so most fires fizzle within ~5-15 cells; the rare one runs longer when wind aligns.
    //   Phase 6C — the optional density factor amplifies this back up in dense plant areas
    //     (0.3× base in empty terrain, 1.0× base in fully-vegetated cells). At 6B baseline (no
    //     vegetation system wired) we land at uniform 1.0× of the constant below.
    //   Plightning calibrated for ~1 strike per 30 s over 160 000 cells × 10 Hz:
    //     160 000 × p × 10 = 1 / 30  →  p ≈ 2.1e-8 ≈ 1 / 48 000 000.
    //     (Cell count quadrupled when Size went 200 → 400, so p drops 4× to keep the same
    //     world-wide strike rate.)
    //   Pregrow gives an empty cell ~125 s mean wait to come back as fuel — long enough that
    //     burn scars stay visible, short enough that the world stays "alive".
    private const float SpreadPerNeighbour = 0.025f;
    private const float Pregrow            = 0.0008f;
    private const float Plightning         = 1f / 48_000_000f;
    private const float WindBias           = 0.5f;
    // Phase 6C — producer-side plant fuel boost. A burning cell whose <see cref="BurnIntensity"/>
    // was captured at full vegetation density (255) contributes 1× <see cref="IntensityBoost"/>
    // extra to each neighbour's spread weight; bare cell (intensity 0) contributes nothing extra.
    // 0.5 keeps the spread sub-critical even at full intensity (4 neighbours × 1.5 = 6 weighted vs
    // 4 unweighted → ~1.5× spread probability — enough to be visible without runaway).
    private const float IntensityBoost     = 0.5f;

    private static bool IsBurning(byte s) => s >= BurnMin && s <= BurnStart;

    // Double-buffered state. State is the current world; _next is the scratch we write to during
    // Tick. The (State, _next) swap is a single reference assignment — atomic on x64, so
    // Godot's render thread reads either the pre-swap or post-swap array, never a torn pair.
    public byte[] State { get; private set; }
    private byte[] _next;

    // Phase 6C — per-cell burn intensity, captured at the moment of ignition from the consumer-side
    // density factor. Persists through the cell's ~1-second burn (frozen value, not decayed) so the
    // "burning plants make fire emanate more strongly" coupling lasts the full burn window. Reset to
    // 0 when the cell transitions out of Burning. Double-buffered alongside <see cref="State"/>.
    public byte[] BurnIntensity { get; private set; }
    private byte[] _intensityNext;

    // LCG RNG — deterministic, no allocations, no shared state. Seed chosen for visual variety
    // across runs but stable within a single run.
    private uint _rng = 0xCAFEF11Eu;

    public FireGrid()
    {
        State = new byte[CellCount];
        _next = new byte[CellCount];
        BurnIntensity = new byte[CellCount];
        _intensityNext = new byte[CellCount];
        // Start with a fully-fueled world — lightning + tool ignition seed the first fires;
        // ash regrows back to Fuel over time. Alternative would be "empty + slow growth" but
        // that gives a boring start for the demo.
        Array.Fill(State, Fuel);
    }

    /// <summary>
    /// Advance the CA one tick. Reads <see cref="State"/>, writes <see cref="_next"/>, swaps.
    /// Wind drives neighbour-spread anisotropy: a Burning cell to the east of a Fuel cell with
    /// east-pointing wind contributes more to that cell's ignition probability than a Burning
    /// cell to the west.
    ///
    /// Phase 6C — <paramref name="densityFactor"/> (optional, length = <see cref="CellCount"/>)
    /// scales per-cell spread probability by <c>0.3 + 0.7 × densityFactor[c] / 255</c>. Sparse
    /// rocky areas → 30 % of base spread; fully-vegetated cells → 100 %. Empty span → uniform 1×.
    /// </summary>
    public void Tick(float windX, float windY, ReadOnlySpan<byte> densityFactor = default)
    {
        var src = State;
        var dst = _next;
        var intSrc = BurnIntensity;
        var intDst = _intensityNext;
        var hasDensity = !densityFactor.IsEmpty;
        const float Inv255 = 1f / 255f;

        for (var y = 0; y < Size; y++)
        {
            var rowOffset = y * Size;
            for (var x = 0; x < Size; x++)
            {
                var c = rowOffset + x;
                var s = src[c];
                byte n;
                byte intensityN = 0;
                if (s >= BurnMin && s <= BurnStart)
                {
                    // Countdown: stay burning while > BurnMin, expire to Empty at BurnMin.
                    // Keep intensity frozen for the duration of the burn; reset to 0 when ash.
                    n = s == BurnMin ? Empty : (byte)(s - 1);
                    intensityN = n == Empty ? (byte)0 : intSrc[c];
                }
                else if (s == Fuel)
                {
                    // Sum wind-weighted burning-neighbour contributions. The weight per
                    // burning neighbour is (1 + bias * dot(dirToCell, wind)), clamped to 0:
                    //   - Neighbour to the west and wind = east-pointing (windX > 0):
                    //     contribution > 1, fire spreads east readily.
                    //   - Same neighbour with wind west-pointing: contribution < 1.
                    // Phase 6C — producer-side: burning neighbours with high BurnIntensity (plants
                    // burning on them) emit more spread weight (× (1 + IntensityBoost × i/255)).
                    var w = 0f;
                    if (y > 0 && IsBurning(src[c - Size]))
                    {
                        var weight = 1f + WindBias * (-1f * windY);
                        if (weight > 0f) w += weight * (1f + IntensityBoost * intSrc[c - Size] * Inv255);
                    }
                    if (y < Size - 1 && IsBurning(src[c + Size]))
                    {
                        var weight = 1f + WindBias * (1f * windY);
                        if (weight > 0f) w += weight * (1f + IntensityBoost * intSrc[c + Size] * Inv255);
                    }
                    if (x > 0 && IsBurning(src[c - 1]))
                    {
                        var weight = 1f + WindBias * (-1f * windX);
                        if (weight > 0f) w += weight * (1f + IntensityBoost * intSrc[c - 1] * Inv255);
                    }
                    if (x < Size - 1 && IsBurning(src[c + 1]))
                    {
                        var weight = 1f + WindBias * (1f * windX);
                        if (weight > 0f) w += weight * (1f + IntensityBoost * intSrc[c + 1] * Inv255);
                    }

                    if (w > 0f)
                    {
                        // Probability that *any* neighbour ignites this cell = 1 - (1 - p)^w.
                        // Consumer-side density (Phase 6C): rescales p in [0.3×, 1.0×] of base.
                        var pBase = SpreadPerNeighbour;
                        if (hasDensity)
                        {
                            var k = densityFactor[c] * Inv255;
                            pBase *= 0.3f + 0.7f * k;
                        }
                        var spread = 1f - MathF.Pow(1f - pBase, w);
                        if (NextRand() < spread)
                        {
                            n = BurnStart;
                            // Capture this cell's plant density at ignition — frozen for the burn.
                            intensityN = hasDensity ? densityFactor[c] : (byte)0;
                        }
                        else
                        {
                            n = Fuel;
                        }
                    }
                    else if (NextRand() < Plightning)
                    {
                        n = BurnStart;
                        intensityN = hasDensity ? densityFactor[c] : (byte)0;
                    }
                    else
                    {
                        n = Fuel;
                    }
                }
                else if (s == Empty)
                {
                    n = NextRand() < Pregrow ? Fuel : Empty;
                }
                else
                {
                    n = s;
                }
                dst[c] = n;
                intDst[c] = intensityN;
            }
        }

        // Atomic single-reference swap (x64). Godot may snapshot State at any moment;
        // it always sees one complete tick's worth of data. BurnIntensity swap follows the same
        // rule — Godot doesn't read it (it's sim-internal) but keep the symmetry.
        (State, _next) = (_next, State);
        (BurnIntensity, _intensityNext) = (_intensityNext, BurnIntensity);
    }

    /// <summary>
    /// Set every Fuel cell within <paramref name="radius"/> cells of (simX, simY) to Burning.
    /// Returns true if any cell was actually flipped (useful for the caller to skip a "no fuel
    /// to ignite" log entry).
    ///
    /// Phase 6C — if <paramref name="densityFactor"/> is provided, captures the cell's plant
    /// density at ignition into <see cref="BurnIntensity"/> for producer-side spread boost.
    /// </summary>
    public bool Ignite(float simX, float simY, int radius, ReadOnlySpan<byte> densityFactor = default)
    {
        var cx = (int)(simX * InvCellSizeSim);
        var cy = (int)(simY * InvCellSizeSim);
        if (cx < 0) cx = 0; else if (cx >= Size) cx = Size - 1;
        if (cy < 0) cy = 0; else if (cy >= Size) cy = Size - 1;

        var x0 = Math.Max(cx - radius, 0);
        var x1 = Math.Min(cx + radius, Size - 1);
        var y0 = Math.Max(cy - radius, 0);
        var y1 = Math.Min(cy + radius, Size - 1);

        var src = State;
        var intensity = BurnIntensity;
        var hasDensity = !densityFactor.IsEmpty;
        var anyFlipped = false;
        for (var y = y0; y <= y1; y++)
        {
            var rowOffset = y * Size;
            for (var x = x0; x <= x1; x++)
            {
                var c = rowOffset + x;
                if (src[c] == Fuel)
                {
                    src[c] = BurnStart;
                    intensity[c] = hasDensity ? densityFactor[c] : (byte)0;
                    anyFlipped = true;
                }
            }
        }
        return anyFlipped;
    }

    /// <summary>LCG (Numerical Recipes constants). Returns a float in [0, 1).</summary>
    private float NextRand()
    {
        _rng = _rng * 1664525u + 1013904223u;
        return (_rng >> 8) * (1f / 16777216f);  // top 24 bits → [0, 1)
    }
}
