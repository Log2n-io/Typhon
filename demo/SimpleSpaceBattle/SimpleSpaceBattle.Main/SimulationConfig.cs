namespace SimpleSpaceBattle;

/// <summary>
/// Every tunable in one place. Ranges are <b>derived from density</b>, not chosen (DESIGN.md §3.3): at
/// ρ = ShipCount / (WorldX·WorldY·WorldZ), the count of ships inside radius R is ρ·4/3πR³, and that count is what
/// makes a real neighbour scan affordable or not.
/// </summary>
public sealed record SimulationConfig
{
    public static SimulationConfig Default { get; } = new();

    // ── World ──────────────────────────────────────────────────────────────
    //
    // A disc, not a cube. Real galaxies are overwhelmingly planar, and since Typhon's cell grid only partitions XY
    // (DESIGN.md §3.2) a 1:5 world is a far smaller distortion under it than a 1:1 one — it cuts the 2D-grid penalty
    // from 11.8× to 3.3×.

    public float WorldX { get; init; } = 1_000f;
    public float WorldY { get; init; } = 1_000f;
    public float WorldZ { get; init; } = 200f;

    /// <summary>
    /// The dominant tuning knob. Candidates per query = ρ·(CellSize + 2R)²·WorldZ, so this sets the broadphase
    /// selectivity outright — clusters give no spatial selectivity inside a cell (§3.2). Swept in §10.4.
    /// </summary>
    public float CellSize { get; init; } = 50f;

    public int ShipCount { get; init; } = 50_000;

    // ── Tick ───────────────────────────────────────────────────────────────

    public int TickRate { get; init; } = 25;
    public float DeltaTime => 1f / TickRate;
    public ulong MaximumCompletedTicks { get; init; } = 45_000;

    // ── Combat ─────────────────────────────────────────────────────────────

    public uint MaximumHealth { get; init; } = 1_000;

    /// <summary>~131 candidates at default density. Scanned only by ships that lost their lock last tick.</summary>
    public float AcquisitionRange { get; init; } = 50f;

    /// <summary>~28 engaged neighbours at default density. Scanned by every ship, every tick — the bulk of the tick.</summary>
    public float WeaponRange { get; init; } = 30f;

    /// <summary>Power of two so the firing test is a mask rather than a modulo.</summary>
    public int FireIntervalTicks { get; init; } = 8;

    public uint DamagePerHit { get; init; } = 25;

    // ── Movement ───────────────────────────────────────────────────────────

    public float CruiseSpeed { get; init; } = 50f;

    /// <summary>Radians/second cap on pursuit turning, so ships arc toward a target instead of snapping to it.</summary>
    public float TurnRate { get; init; } = 2f;

    public ulong Seed { get; init; } = 0x51_4D_50_4C_53_42_54_00UL;

    // ── Dispatch ───────────────────────────────────────────────────────────

    /// <summary>
    /// Chunk oversubscription for <see cref="ResolutionSystem"/>: it dispatches
    /// <c>WorkerCount × ResolutionChunksPerWorker</c> chunks.
    /// <para>
    /// At 1, each worker gets exactly one chunk and the phase lasts as long as the slowest one — there is no spare
    /// chunk for a finished worker to steal. That matters here because chunk cost is <b>not</b> uniform: a chunk's
    /// work is the sum of its clusters' candidate counts, and clusters in dense regions gather far more than clusters
    /// in sparse ones. Oversubscribing gives the dynamic dispatch loop something to rebalance with.
    /// </para>
    /// <para>Only Resolution oversubscribes. Targeting and Movement are ~0.1–0.6 ms; splitting them finer would add
    /// dispatch overhead to systems that have no imbalance to smooth.</para>
    /// </summary>
    public int ResolutionChunksPerWorker { get; init; } = 2;

    // ── Derived ────────────────────────────────────────────────────────────

    public float WeaponRangeSq => WeaponRange * WeaponRange;

    public float Density => ShipCount / (WorldX * WorldY * WorldZ);

    /// <summary>Expected ships inside <see cref="WeaponRange"/> — the engagement density (§3.3).</summary>
    public float ExpectedNeighbours => Density * 4f / 3f * MathF.PI * WeaponRange * WeaponRange * WeaponRange;

    /// <summary>Predicted narrowphase candidates per weapon-range query: ρ·(CellSize + 2R)²·WorldZ (§3.2).</summary>
    public float PredictedCandidates
    {
        get
        {
            float span = CellSize + 2f * WeaponRange;
            return Density * span * span * WorldZ;
        }
    }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ShipCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TickRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CellSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WeaponRange);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCompletedTicks);

        if (AcquisitionRange < WeaponRange)
        {
            throw new ArgumentException(
                $"AcquisitionRange ({AcquisitionRange}) must be >= WeaponRange ({WeaponRange}): ships acquire before they can shoot.");
        }

        if (FireIntervalTicks < 1 || (FireIntervalTicks & (FireIntervalTicks - 1)) != 0)
        {
            throw new ArgumentException($"FireIntervalTicks ({FireIntervalTicks}) must be a power of two — the firing test is a mask.");
        }
    }
}
