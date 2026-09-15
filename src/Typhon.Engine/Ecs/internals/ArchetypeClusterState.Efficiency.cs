using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// The maintenance budget set from the queries' efficiency (#906, rule TH-04): each tick the archetype's re-clustering budget is
/// <c>SpatialGridConfig.ReclusterBudgetMs</c> scaled by how far its range queries' candidates per hit sit above the best they have shown.
/// </summary>
/// <remarks>
/// <para><b>What it replaces.</b> A fixed budget spends the same every tick whether the partition needs it or not. Measured on the SWG Tatooine workload,
/// most of that spend was churn — cells re-packed tick after tick for a gain the next ticks undid. The cooldown (RP-07) stops the churn; this decides how
/// much to spend at all, from what the spending buys: the entities a query tests per match (SO-02).</para>
/// <para><b>Proportional, around the best seen.</b> <c>scale = (smoothed / best - 1) / tolerance</c>, clamped to
/// [<see cref="MinMaintenanceBudgetScale"/>, 1]: next to nothing at the best, the whole configured budget at <c>tolerance</c> above it. Never zero,
/// because the throttle reads a zero budget as "no enforcement", the opposite of doing nothing.</para>
/// <para><b>Smoothed as sums.</b> Candidates and hits are each averaged over about twenty ticks and then divided, never averaged as per-tick ratios, so a
/// tick with a handful of hits weighs what its handful is worth. The experiment averaged the ratios; at the demo's volumes the two agree.</para>
/// <para><b>The best is only ever lowered, until the whole budget fails.</b> It first rose 1e-4 a tick while the queries stayed above it, so that a world
/// whose shape changes for good would not hold the target out of reach. That rise followed any decline slower than itself, and a starved archetype declines
/// slowly: on the SWG demo it carried Creature's best up with its queries, 5–8 % in 1 000 ticks, and the controller never spent. Now a decline raises the
/// budget until maintenance holds it, and only after <see cref="EfficiencyRebaseTicks"/> ticks at the whole budget is the present level taken as the best:
/// what the whole budget could not recover in that time is the world's. A lasting level within the tolerance is never taken as the best: it keeps its
/// share of the budget, erring toward spending, since taking it is what the creep did.</para>
/// </remarks>
internal sealed partial class ArchetypeClusterState
{
    /// <summary>Weight of one tick in the smoothed candidates and hits: about twenty ticks of memory, as measured.</summary>
    private const double EfficiencySmoothing = 0.05d;

    /// <summary>
    /// Consecutive ticks at the whole budget after which a further one re-bases the best to the queries' present level. Long enough for the whole budget to
    /// show what it recovers: several times the smoothing's settling (about sixty ticks) and the default <c>RepairCooldownTicks</c> of 50, during which a
    /// repaired cell is not repaired again. Paired with that default, not derived from it: a cooldown much longer than 50 slows recovery and wants a longer
    /// window. Chosen, not measured.
    /// </summary>
    internal const int EfficiencyRebaseTicks = 200;

    /// <summary>The smallest scale. Positive: a zero budget means no enforcement to the throttle.</summary>
    internal const double MinMaintenanceBudgetScale = 1e-6d;

    /// <summary>
    /// Smoothed hits per tick from which the queries say enough to steer by. The signal is lost only below half of it, so an archetype queried near the
    /// threshold does not flip between the floor and the whole budget from one tick to the next.
    /// </summary>
    private const double MinSmoothedHitsPerTick = 1d;

    private double _queryCandidatesEwma;
    private double _queryHitsEwma;
    private double _bestQueryCandidatesPerHit = double.PositiveInfinity;
    private bool _hasQuerySignal;
    private int _ticksAtWholeBudget;

    /// <summary>Re-bases since this cluster state was created. Cumulative, in the trace's record (kind 66) as well as here.</summary>
    internal long TotalEfficiencyRebases;

    /// <summary>Whether this tick's <see cref="UpdateMaintenanceBudgetScale"/> re-based the best.</summary>
    internal bool LastTickEfficiencyRebased;

    /// <summary>
    /// This tick's share of the configured budget, in [<see cref="MinMaintenanceBudgetScale"/>, 1]. Set by <see cref="UpdateMaintenanceBudgetScale"/>.
    /// </summary>
    internal double MaintenanceBudgetScale = 1d;

    /// <summary>The budget this tick granted, in milliseconds: <c>ReclusterBudgetMs</c> times <see cref="MaintenanceBudgetScale"/>.</summary>
    internal double LastTickReclusterBudgetGrantedMs;

    /// <summary>Smoothed candidates per tick — the numerator of <see cref="QueryCandidatesPerHitSmoothed"/>, summed by the engine-wide telemetry.</summary>
    internal double QueryCandidatesEwma => _queryCandidatesEwma;

    /// <summary>Smoothed hits per tick — the denominator of <see cref="QueryCandidatesPerHitSmoothed"/>.</summary>
    internal double QueryHitsEwma => _queryHitsEwma;

    /// <summary>
    /// True when the queries hit enough to steer by: gained at <see cref="MinSmoothedHitsPerTick"/> smoothed hits a tick, lost below half of it.
    /// </summary>
    internal bool HasQuerySignal => _hasQuerySignal;

    /// <summary>Consecutive ticks at the whole budget, set there by the distance: the re-base window's progress.</summary>
    internal int TicksAtWholeBudget => _ticksAtWholeBudget;

    /// <summary>Whether this archetype has anything to spend a maintenance budget on: a dynamic spatial field, which relocates and repairs.</summary>
    internal bool SpendsMaintenance => SpatialSlot.HasSpatialIndex && SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic;

    /// <summary>The controller's state in one byte for the trace (kind 66): bit 0 the queries hit enough to steer by, bit 1 this tick re-based.</summary>
    internal byte ControllerFlags => (byte)((_hasQuerySignal ? 1 : 0) | (LastTickEfficiencyRebased ? 2 : 0));

    /// <summary>The controller's input: smoothed candidates over smoothed hits. Zero without a signal.</summary>
    internal double QueryCandidatesPerHitSmoothed => HasQuerySignal ? _queryCandidatesEwma / _queryHitsEwma : 0d;

    /// <summary>
    /// The lowest <see cref="QueryCandidatesPerHitSmoothed"/> since the last re-base (<see cref="EfficiencyRebaseTicks"/>), or since the first signal. Zero
    /// without a signal.
    /// </summary>
    internal double QueryCandidatesPerHitBest =>
        HasQuerySignal && !double.IsPositiveInfinity(_bestQueryCandidatesPerHit) ? _bestQueryCandidatesPerHit : 0d;

    /// <summary>
    /// Fold this tick's query tally into the smoothed signal and set <see cref="MaintenanceBudgetScale"/>. Called once per archetype per fence, from its
    /// per-tick reset, right after the tally's delta and before anything spends the budget.
    /// </summary>
    internal void UpdateMaintenanceBudgetScale(in SpatialGridConfig cfg)
    {
        LastTickEfficiencyRebased = false;
        _queryCandidatesEwma += EfficiencySmoothing * (LastTickQueryCandidates - _queryCandidatesEwma);
        _queryHitsEwma += EfficiencySmoothing * (LastTickQueryHits - _queryHitsEwma);
        _hasQuerySignal = _hasQuerySignal ? _queryHitsEwma >= MinSmoothedHitsPerTick * 0.5d : _queryHitsEwma >= MinSmoothedHitsPerTick;

        // Every archetype has a cluster state and its queries are tallied, static halves included, but only one with a dynamic spatial field relocates or
        // repairs. The others are granted nothing, and keep no streak and take no re-base either: those would describe a budget they never had.
        var spends = SpendsMaintenance;

        // No signal — no range query for a while, or only kinds the tally does not count — and the configured budget stands, exactly as without the
        // controller. The experiment granted next to nothing here instead, which starves a world queried only by rays or frustums.
        var scale = 1d;
        var atWholeBudget = false;
        if (_hasQuerySignal)
        {
            var smoothed = _queryCandidatesEwma / _queryHitsEwma;
            _bestQueryCandidatesPerHit = Math.Min(_bestQueryCandidatesPerHit, smoothed);
            var tolerance = cfg.QueryEfficiencyTolerance;
            var distance = (smoothed / _bestQueryCandidatesPerHit) - 1d;

            // Not NaN, which would pass every "budget <= 0" test downstream and read as no enforcement. Candidates never fall below hits (SO-02), so
            // nothing reaches it today; the guard is what keeps it that way.
            if (tolerance > 0f && double.IsFinite(distance))
            {
                scale = Math.Clamp(distance / tolerance, MinMaintenanceBudgetScale, 1d);
                atWholeBudget = spends && scale >= 1d;

                // The re-base, decided on this tick's own distance: a tick the whole budget has just brought back inside the tolerance never re-bases.
                if (atWholeBudget && _ticksAtWholeBudget >= EfficiencyRebaseTicks)
                {
                    _bestQueryCandidatesPerHit = smoothed;
                    scale = MinMaintenanceBudgetScale;
                    atWholeBudget = false;
                    LastTickEfficiencyRebased = true;
                    TotalEfficiencyRebases++;
                }
            }
        }

        // Counted only where the distance set the scale. The whole budget granted without a signal, with the controller off, or past a distance that is not
        // finite says nothing about what maintenance can recover, and must not re-base the best.
        _ticksAtWholeBudget = atWholeBudget ? _ticksAtWholeBudget + 1 : 0;
        MaintenanceBudgetScale = scale;

        // Granted only where there is something to spend it on. Reporting the configured budget for the others made the engine-wide total overstate the
        // grant by one budget per archetype.
        LastTickReclusterBudgetGrantedMs = spends ? cfg.ReclusterBudgetMs * scale : 0d;
    }

    /// <summary>
    /// The budget every consumer spends this tick, in nanoseconds: the repair planner, the throttle and the drift scan's nomination cap. Zero exactly when
    /// <c>ReclusterBudgetMs</c> is zero, which keeps meaning no enforcement.
    /// </summary>
    internal double MaintenanceBudgetNs(in SpatialGridConfig cfg) => cfg.ReclusterBudgetMs * 1_000_000d * MaintenanceBudgetScale;
}
