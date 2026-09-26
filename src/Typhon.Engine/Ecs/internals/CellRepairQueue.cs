using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace Typhon.Engine.Internals;

/// <summary>
/// The persistent, lazily-ranked set of cells waiting to be repaired — #872 step 11's priority queue (design §5.6).
/// </summary>
/// <remarks>
/// <para><b>What it replaces.</b> Step 12 kept nominations in a <c>List&lt;int&gt;</c> that the planner drained and cleared every tick, and ordered them
/// with <c>Array.Sort</c> on the cell KEY. Two consequences, both stated in that code's own remarks as step 11's to fix: a cell whose unit the budget could
/// not afford was <b>forgotten</b> rather than deferred, and the order in which cells were tried was arbitrary with respect to how much repairing any of
/// them would buy. This structure fixes the first by outliving the tick and the second by ranking.</para>
/// <para><b>Round-robin is explicitly the wrong policy</b> (§5.6): "a region nobody queries never needs tight clusters". So candidates are ranked by
/// expected selectivity gain — <c>degradation x tierWeight x clusterCount</c> — and aged so that ranking cannot starve anyone (<c>AC-11.3</c>).</para>
/// <para><b>Per archetype, never shared.</b> The scores read <c>CellClusterPool</c>, which is per-archetype, and the units it schedules are that
/// archetype's clusters. Two archetypes over one cell are two independent candidates, which is correct: they degrade and are repaired independently.</para>
/// <para><b>Transient, and owes the WAL nothing.</b> Every field here is derived from cluster bounds that are themselves rebuilt at startup. A crash loses
/// the queue and the next tick's AABB pass re-nominates whatever still deserves it.</para>
/// <para><b>Keyed by (realm, cell) — Realms D2, decision D-6.</b> One queue per archetype, whatever the realm count: a candidate is a cell of one realm
/// (<see cref="Key"/>), scored against that realm's grid and cell cluster pool, so tidying is ranked by need across realms and the archetype's one budget
/// stays bounded. A realm that is not runnable keeps its candidates; the planner skips them until it runs again.</para>
/// <para><b>Single-threaded by contract.</b> Every method is called from Prep, which runs one work item per archetype. Nomination — the parallel half —
/// goes into <c>ArchetypeClusterState.RepairNominations</c> under the finalize lock and is folded in here by <see cref="Absorb"/>.</para>
/// <para><b>A repaired cell cools before it can queue again</b> (<c>RP-07</c>, <c>SpatialGridConfig.RepairCooldownTicks</c>). Under motion a re-packed
/// cell decays within a few ticks and is nominated again, and re-packing it every time is the churn the default spent its budget on. So a cell the planner
/// has just repaired is held outside the candidate set until its cooldown ends; what it is nominated for meanwhile is kept and handed back then.</para>
/// </remarks>
internal sealed class CellRepairQueue
{
    /// <summary>One cell waiting for service, and everything the ranking needs to know about it.</summary>
    private struct Candidate
    {
        /// <summary>Worst degradation any of the cell's clusters has reported since it was last serviced — max axis extent over cell size.</summary>
        /// <remarks>
        /// The <b>max</b> across nominations, not the mean or the latest. A cell with one catastrophic cluster and nine tight ones deserves servicing
        /// ahead of one with ten mediocre ones: the repair unit is the cell's WORST clusters, so the worst is what predicts the gain. Taking the latest
        /// instead would make the score depend on which cluster happened to be written last.
        /// </remarks>
        internal float Degradation;

        /// <summary>Tick on which this candidate was first queued and not yet serviced — the input to the aging term.</summary>
        internal long WaitingSinceTick;

        /// <summary>Score as of the last time this candidate was scored — by a re-rank, or by <see cref="Absorb"/> when it arrived or was re-degraded.</summary>
        /// <remarks>
        /// Advisory, and deliberately not trusted where it matters: it ages out silently, because the age factor grows every tick while the cached value
        /// does not. <see cref="TryEvictWorst"/> re-scores rather than reading it, for exactly that reason.
        /// </remarks>
        internal float Score;
    }

    /// <summary>Hard cap on live candidates, so a permanently over-subscribed queue cannot grow without bound (<c>AC-11.8</c>).</summary>
    private readonly int _maxCells;

    /// <summary>Per-tick multiplier applied to a candidate's age. See <see cref="Score"/>.</summary>
    private readonly float _agingRatePerTick;

    private readonly Dictionary<long, Candidate> _candidates = [];

    /// <summary>The ranked candidate keys produced by the last <see cref="Rerank"/>, best first. Only <see cref="_rankedCount"/> entries are valid.</summary>
    private long[] _ranked = [];

    private int _rankedCount;

    /// <summary>Scratch parallel to <see cref="_ranked"/>, holding scores so the sort does not re-enter the dictionary per comparison.</summary>
    private float[] _rankedScores = [];

    /// <summary>Nominations absorbed since the last re-rank. A rank whose inputs have not changed is a rank not worth paying for.</summary>
    private int _dirtySinceRank;

    /// <summary>The engine-wide tier version the last re-rank saw, so a tier flip in any realm invalidates the order that used it.</summary>
    private int _rankedTierVersion = -1;

    /// <summary>Ticks a repaired cell spends outside the candidate set; <c>0</c> disables the cooldown.</summary>
    private readonly int _cooldownTicks;

    /// <summary>
    /// Cells waiting out a cooldown, each mapped to the worst degradation nominated for it since its repair — <c>0</c> when nothing nominated it. Disjoint
    /// from <see cref="_candidates"/>: a cell is in one, the other, or neither. When each cooldown ends is <see cref="_coolingOrder"/>'s business.
    /// </summary>
    private readonly Dictionary<long, float> _cooling = [];

    /// <summary>The same cells in the order they were repaired — which, with one cooldown for every cell, is the order they are released in.</summary>
    private readonly Queue<(long Key, long ReleaseTick)> _coolingOrder = new();

    /// <summary>Candidates dropped because the queue was full, since this queue was created.</summary>
    internal long TotalEvicted;

    /// <summary><see cref="System.Diagnostics.Stopwatch"/> ticks spent in <see cref="Absorb"/> and <see cref="Rerank"/> during the last tick —
    /// <c>AC-11.5</c>'s numerator.</summary>
    internal long LastTickMaintenanceTicks;

    /// <summary>The candidate key of cell <paramref name="cellKey"/> in realm <paramref name="realm"/>: a cell key names a cell in every realm.</summary>
    internal static long Key(ushort realm, int cellKey) => ((long)realm << 32) | (uint)cellKey;

    /// <summary>The realm of candidate key <paramref name="key"/>.</summary>
    internal static ushort RealmOf(long key) => (ushort)(key >> 32);

    /// <summary>The cell of candidate key <paramref name="key"/>, within its realm's grid.</summary>
    internal static int CellOf(long key) => (int)(uint)key;

    internal CellRepairQueue(int maxCells, float agingRatePerTick, int cooldownTicks = 0)
    {
        _maxCells = Math.Max(1, maxCells);
        _agingRatePerTick = Math.Max(0f, agingRatePerTick);
        _cooldownTicks = Math.Max(0, cooldownTicks);
    }

    /// <summary>Cells currently waiting. The queue-depth telemetry, and the denominator for the eviction rate.</summary>
    internal int Count => _candidates.Count;

    /// <summary>Cells waiting out a repair cooldown. A level, like <see cref="Count"/>, and never counted in it.</summary>
    internal int CoolingCount => _cooling.Count;

    /// <summary>
    /// Whether the planner has work here on <paramref name="tickNumber"/> even with nothing newly nominated: a candidate waiting, or a cooldown ending.
    /// </summary>
    /// <remarks>
    /// The fence's early-out asks this, not <see cref="Count"/>. A cooling cell is not a candidate, so a <see cref="Count"/> test skips the planner on a
    /// tick with no nomination and no candidate — and the planner is what calls <see cref="ReleaseCooled"/>, so a cell that went still while it cooled
    /// would stay out of the queue until some other cell happened to nominate (RP-07).
    /// </remarks>
    internal bool NeedsPlanning(long tickNumber) => _candidates.Count > 0 || (_coolingOrder.TryPeek(out var next) && next.ReleaseTick <= tickNumber);

    /// <summary>Ranked candidate keys (<see cref="Key"/>), best first — valid only immediately after <see cref="Rerank"/>.</summary>
    internal ReadOnlySpan<long> Ranked => _ranked.AsSpan(0, _rankedCount);

    /// <summary>
    /// Fold one tick's nominations into the persistent set, keeping the worst degradation per cell.
    /// </summary>
    /// <remarks>
    /// <para>The caller's list is <b>not</b> cleared here — <c>PlanCellRepairs</c> owns that, and it must happen even on the paths that never reach this
    /// method, or an archetype that stops meeting the planner's preconditions accumulates nominations for ever.</para>
    /// <para><b>Eviction happens at most once per absorbed nomination, and only when the queue is full.</b> The victim is chosen by re-scoring every live
    /// candidate against the current tick — see <see cref="TryEvictWorst"/> for why the cached score cannot be used — so the scan is O(n) in the candidates
    /// but runs only in the over-subscribed case it exists for.</para>
    /// </remarks>
    internal void Absorb(List<ArchetypeClusterState.RepairNomination> nominations, ArchetypeClusterState state, long tickNumber)
    {
        for (var i = 0; i < nominations.Count; i++)
        {
            var nomination = nominations[i];
            var key = Key(nomination.Realm, nomination.CellKey);

            // HELD, neither admitted nor dropped (RP-07). The cell was repaired too recently to be a candidate, but the evidence is kept and handed back
            // by ReleaseCooled. Dropping it would lose a cell that goes still while it cools: in barrier-only mode nothing nominates a cell nobody writes
            // (RP-04's known gap), so this nomination may be the last one it ever gets.
            if (_cooling.Count > 0 && _cooling.TryGetValue(key, out var held))
            {
                if (nomination.Degradation > held)
                {
                    _cooling[key] = nomination.Degradation;
                }

                continue;
            }

            Admit(key, nomination.Degradation, state, tickNumber);
        }
    }

    /// <summary>Fold one degradation reading into the candidate set: raise an existing candidate's, or queue a new candidate, evicting at the cap.</summary>
    private void Admit(long key, float degradation, ArchetypeClusterState state, long tickNumber)
    {
        if (_candidates.TryGetValue(key, out var existing))
        {
            if (degradation > existing.Degradation)
            {
                existing.Degradation = degradation;

                // Re-scored, not just re-degraded. TryEvictWorst picks its victim on the CACHED score, so a cell whose degradation has just tripled
                // would otherwise carry its pre-nomination score into the victim scan and lose to a mediocre newcomer scored fresh — evicting the
                // candidate that most deserves servicing, at the exact moment it became the most deserving.
                existing.Score = Score(key, in existing, state, tickNumber);
                _candidates[key] = existing;
                _dirtySinceRank++;
            }

            return;
        }

        var candidate = new Candidate
        {
            Degradation = degradation,
            WaitingSinceTick = tickNumber,
            Score = 0f,
        };

        // Scored BEFORE the eviction test, and the ordering is the whole difference between eviction and thrashing.
        //
        // An unscored newcomer enters at 0, which is below every ranked candidate — so the next newcomer of the same batch evicts IT, and the one
        // after that evicts the second. Only the last nomination of a batch would survive, TotalEvicted would be inflated by the churn, and the
        // eviction policy would be last-writer-wins wearing a ranking as a disguise. Scoring first makes the victim scan compare like with like.
        candidate.Score = Score(key, in candidate, state, tickNumber);

        if (_candidates.Count >= _maxCells && !TryEvictWorst(candidate.Score, state, tickNumber))
        {
            // Every live candidate outranks the newcomer, so admitting it would mean evicting something better. Dropped, and counted: a non-zero
            // eviction rate against a full queue is the reading that says the cap is below what the world actually degrades.
            TotalEvicted++;
            return;
        }

        _candidates[key] = candidate;
        _dirtySinceRank++;
    }

    /// <summary>
    /// Forget a cell the planner has just repaired and, when a cooldown is configured, hold it out of the candidate set until the cooldown ends (<c>RP-07</c>).
    /// </summary>
    /// <remarks>
    /// Called only for a unit that MOVED entities. A unit that moved nothing — already packed, a single cluster, a population below two — was not a repair,
    /// and <see cref="Remove"/> is its path: RP-03's no-op memo already stops it recurring, and a cooldown would make the cell's next genuine degradation wait
    /// out a repair that never happened.
    /// </remarks>
    internal void MarkRepaired(long key, long tickNumber)
    {
        // A cooling cell is never a candidate, so the planner cannot have repaired one — and a second cooldown would leave one cell two FIFO entries.
        Debug.Assert(!_cooling.ContainsKey(key), "a cooling cell was repaired");
        Remove(key);
        if (_cooldownTicks == 0)
        {
            return;
        }

        _cooling[key] = 0f;
        _coolingOrder.Enqueue((key, tickNumber + _cooldownTicks));
    }

    /// <summary>
    /// End every cooldown due by <paramref name="tickNumber"/>, and queue each released cell that was nominated while it cooled, at the worst degradation
    /// seen — whether or not it is nominated again.
    /// </summary>
    /// <remarks>
    /// <para>A cell nothing nominated while it cooled is simply released: it had nothing left to repair, and the next nomination queues it as usual. A released
    /// cell enters through the same door as any newcomer, so at <c>RepairQueueMaxCells</c> it can be evicted (TH-03).</para>
    /// <para><b>O(released), not O(cooling).</b> Every cell cools for the same number of ticks and <see cref="MarkRepaired"/> never sees a cooling cell, so
    /// each cooling cell has exactly one FIFO entry and release order is repair order. That rests on tick numbers increasing from fence to fence, which the
    /// fence already requires: a repeated tick never ends a cooldown, and a decreasing one delays releases behind an older head. Neither corrupts
    /// anything.</para>
    /// </remarks>
    internal void ReleaseCooled(ArchetypeClusterState state, long tickNumber)
    {
        while (_coolingOrder.TryPeek(out var next) && next.ReleaseTick <= tickNumber)
        {
            _coolingOrder.Dequeue();
            if (_cooling.Remove(next.Key, out var held) && held > 0f)
            {
                Admit(next.Key, held, state, tickNumber);
            }
        }
    }

    /// <summary>
    /// Rebuild the ranked order if anything that feeds it has changed, and return whether a rank actually ran.
    /// </summary>
    /// <remarks>
    /// <para><b>Lazy on two independent signals</b>, because §5.6 requires the queue to cost less than the work it schedules and a full sort every tick
    /// would not. A rank runs when nominations have arrived since the last one, or when <c>SpatialGrid.TierVersion</c> has moved — the grid already bumps
    /// that only when a cell's tier actually flips (<c>SpatialGrid.SetCellTier</c>), so it is exactly the "has the query-frequency signal changed"
    /// question, already answered and free to read.</para>
    /// <para>Aging is applied at <b>score</b> time rather than by re-sorting on a timer, so a quiet tick still costs nothing: the order only goes stale in
    /// the direction of under-serving old candidates, and the next rank — triggered by the next nomination anywhere in the archetype — corrects it.</para>
    /// </remarks>
    /// <remarks><c>tierVersion</c> is the engine-wide one (<see cref="RealmTable.TierVersion"/>): a tier flip in any realm re-weights candidates.</remarks>
    internal bool Rerank(int tierVersion, ArchetypeClusterState state, long tickNumber)
    {
        if (_dirtySinceRank == 0 && tierVersion == _rankedTierVersion && _rankedCount == _candidates.Count)
        {
            return false;
        }

        var count = _candidates.Count;
        if (_ranked.Length < count)
        {
            var grown = Math.Max(count, Math.Max(16, _ranked.Length * 2));
            _ranked = new long[grown];
            _rankedScores = new float[grown];
        }

        var n = 0;
        foreach (var pair in _candidates)
        {
            _ranked[n] = pair.Key;
            _rankedScores[n] = -Score(pair.Key, pair.Value, state, tickNumber);   // negated so an ascending sort yields best-first, with no comparer
            n++;
        }

        // The cached scores are written back AFTER the enumeration, not inside it. Overwriting an existing key's value during a foreach happens not to
        // invalidate a Dictionary's enumerator — TryInsert's overwrite path leaves _version alone, and it was verified on .NET 10 rather than assumed —
        // but the documentation promises that only for Remove and Clear. Doing it in a second pass costs one more walk of an array that is already in
        // cache and rests on nothing unpublished.
        for (var i = 0; i < n; i++)
        {
            var key = _ranked[i];
            var candidate = _candidates[key];
            candidate.Score = -_rankedScores[i];
            _candidates[key] = candidate;
        }

        // Keys carried as the items so the sort is over two primitive arrays rather than through an IComparer on a struct — and the key array IS the
        // output, so nothing is copied afterwards.
        Array.Sort(_rankedScores, _ranked, 0, n);
        _rankedCount = n;
        _dirtySinceRank = 0;
        _rankedTierVersion = tierVersion;
        return true;
    }

    /// <summary>
    /// Expected selectivity gain from repairing one cell: <c>degradation x tierWeight x clusterCount x ageFactor</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>degradation</b> — how far the worst cluster's bound has spread across its cell. Recorded at nomination, where the AABB pass already had it
    /// in registers.</para>
    /// <para><b>tierWeight</b> — §5.6's "query frequency" signal, and the one that changes behaviour most: a region nobody queries never needs tight
    /// clusters. <c>CellState.Tier</c> is the simulation tier game code assigns a cell (<c>SetCellTier</c>), not a measured query counter —
    /// measuring would mean a write on the query READ path, which is a scalability cost paid by every query to serve a heuristic. See
    /// <see cref="TierWeight"/> for why <see cref="SimTier.None"/> is not simply "the lowest tier".</para>
    /// <para><b>clusterCount</b> — §5.6 names <c>CellState.EntityCount</c> for population; this uses the archetype's own cluster count instead, and the
    /// deviation is deliberate. <c>CellState.EntityCount</c> is a GRID-WIDE sum across every archetype sharing the grid, so in a per-archetype queue it
    /// over-weights any cell that several archetypes happen to occupy. The cluster count is this archetype's own and is what the unit's cost is
    /// proportional to.</para>
    /// <para><b>ageFactor</b> — unbounded in the tick count, which is what makes <c>AC-11.3</c> true rather than likely: whatever a candidate's base
    /// score, enough ticks of waiting carry it to the head. Ranking alone starves; §5.6 asks for ranking, not for starvation.</para>
    /// </remarks>
    private float Score(long key, in Candidate candidate, ArchetypeClusterState state, long tickNumber)
    {
        // The candidate's OWN realm: its grid for the tier weight, its cluster pool for the count. A realm whose state is gone scores the floor.
        var rs = SpatialOfRealm(state, RealmOf(key));
        var cellKey = CellOf(key);
        var pool = rs?.CellClusterPool;
        var clusters = pool != null ? pool.GetClusters(cellKey) : default;
        if (clusters.Length < 2)
        {
            // A single cluster is its own optimal packing — a sort cannot improve a partition of one. Scored to the floor rather than removed here,
            // because removal during a rank would mutate the dictionary being enumerated; the planner drops it when it declines the unit.
            return 0f;
        }

        // A candidate of a realm that is not runnable does not age (review #4): unbounded ageing would carry waiting dormant candidates to the head and
        // leave fresh runnable ones at the tail, where eviction takes its victims.
        var age = tickNumber - candidate.WaitingSinceTick;
        var realms = state.RealmTableOrNull;
        var ageFactor = realms != null && !realms.IsRunnable(RealmOf(key)) ? 1f : 1f + (_agingRatePerTick * (age > 0 ? age : 0));
        return candidate.Degradation * TierWeight(rs.Grid, cellKey) * clusters.Length * ageFactor;
    }

    /// <summary>The archetype's spatial state in <paramref name="realm"/>, or null when it has none there.</summary>
    private static RealmArchetypeSpatial SpatialOfRealm(ArchetypeClusterState state, ushort realm)
    {
        var byRealm = state.RealmSpatial;
        return byRealm != null && realm < byRealm.Length ? byRealm[realm] : null;
    }

    /// <summary>
    /// Turn a cell's <see cref="SimTier"/> into a multiplier in <c>(0, 1]</c>, highest interest weighing most.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="SimTier"/> is a BIT FLAG, not an ordinal</b> — <c>None = 0, Tier0 = 1, Tier1 = 2, Tier2 = 4, Tier3 = 8</c>. Weighting by the
    /// byte would be exponential in the tier rather than linear, so the index is recovered with
    /// <see cref="BitOperations.TrailingZeroCount(uint)"/> and the weight is <c>1 / (1 + index)</c>: 1, ½, ⅓, ¼.</para>
    /// <para><b><see cref="SimTier.None"/> means "no tier information", NOT "the least interesting cell"</b>, and getting that backwards would have
    /// disabled the ranking everywhere it is not configured. A world whose game code assigns no tier leaves every cell at the grid's default tier,
    /// which starts as zero — and <c>TrailingZeroCount(0)</c> is 32, so the naive formula would score every cell in such a world at 1/33 and make the
    /// whole ranking a rounding error. Weighted 1.0 instead: absent information discounts nothing, and the score degrades to
    /// <c>degradation x clusterCount x ageFactor</c>, which is exactly what it should be when nobody has said which regions are watched.</para>
    /// </remarks>
    private static float TierWeight(SpatialGrid grid, int cellKey)
    {
        if (grid == null || (uint)cellKey >= (uint)grid.CellCount)
        {
            return 1f;
        }

        var tier = grid.GetCell(cellKey).Tier;
        if (tier == 0)
        {
            return 1f;
        }

        return 1f / (1 + BitOperations.TrailingZeroCount((uint)tier));
    }

    /// <summary>Drop the worst-ranked live candidate to make room, unless the newcomer is worse than it.</summary>
    /// <remarks>
    /// <para><b>The victim is found from the TAIL of the last ranking, not by scanning the dictionary.</b> <see cref="_ranked"/> is ordered best-first, so
    /// its live tail is the worst candidate the last rank knew about — one probe in the common case, against a full enumeration of up to
    /// <c>RepairQueueMaxCells</c> entries per new cell key. That enumeration is what the previous version did, and at the 4 096 default it made a burst of
    /// nominations against a full queue quadratic.</para>
    /// <para><b>The candidate it finds is then RE-SCORED before being compared.</b> A cached score is as old as the last <see cref="Rerank"/> and its
    /// age factor with it, so an incumbent that has waited fifty ticks would be judged at the score it had on arrival while every newcomer is scored fresh
    /// — biasing eviction against precisely the long-waiting candidates the age term exists to protect, which is <c>TH-03</c>'s starvation reintroduced
    /// through the back door.</para>
    /// <para><b>What the tail costs in accuracy is stated rather than hidden:</b> if the ranking is stale the true worst may no longer be at the end, so
    /// this evicts an approximately-worst candidate rather than the worst. That is acceptable for a heuristic queue whose whole output is an ordering
    /// preference, and it errs by keeping a slightly worse cell rather than by dropping a better one — the re-score is what rules out the second.</para>
    /// </remarks>
    private bool TryEvictWorst(float incomingScore, ArchetypeClusterState state, long tickNumber)
    {
        for (var i = _rankedCount - 1; i >= 0; i--)
        {
            var key = _ranked[i];
            if (!_candidates.TryGetValue(key, out var candidate))
            {
                continue;   // serviced or already evicted since the last rank
            }

            // A tie keeps the incumbent, so a batch of identical nominations against a full queue evicts nothing rather than churning through it.
            if (incomingScore <= Score(key, in candidate, state, tickNumber))
            {
                return false;
            }

            _candidates.Remove(key);
            TotalEvicted++;
            _dirtySinceRank++;
            return true;
        }

        // The ranking holds nothing live — every entry was serviced since the last rank, or none has ever been produced. Fall back to any candidate, which
        // cannot be worse than admitting nothing: the queue is at its cap, so somebody has to go.
        foreach (var pair in _candidates)
        {
            _candidates.Remove(pair.Key);
            TotalEvicted++;
            _dirtySinceRank++;
            return true;
        }

        return false;
    }

    /// <summary>Drops every candidate and cooling entry of <paramref name="realm"/> — a removed realm (Realms D5). O(queue), rare.</summary>
    internal void RemoveRealm(ushort realm)
    {
        List<long> gone = null;
        foreach (var key in _candidates.Keys)
        {
            if (RealmOf(key) == realm)
            {
                (gone ??= []).Add(key);
            }
        }

        foreach (var key in _cooling.Keys)
        {
            if (RealmOf(key) == realm)
            {
                (gone ??= []).Add(key);
            }
        }

        if (gone == null)
        {
            return;
        }

        foreach (var key in gone)
        {
            _candidates.Remove(key);
            _cooling.Remove(key);   // its FIFO entry releases nothing when it comes due: the cooling map no longer holds it
        }

        _dirtySinceRank++;
    }

    /// <summary>Forget one cell — called when the planner declines it as unrepairable. A cell it services goes through <see cref="MarkRepaired"/>.</summary>
    internal void Remove(long key)
    {
        if (_candidates.Remove(key))
        {
            _dirtySinceRank++;
        }
    }

    /// <summary>The degradation recorded for a queued cell, or <c>0</c> when it is not queued. Drives the safety valve's threshold test.</summary>
    internal float DegradationOf(long key) => _candidates.TryGetValue(key, out var candidate) ? candidate.Degradation : 0f;

    /// <summary>Whether the candidate set is at its hard cap, so the next admission has to evict one (<c>AC-11.8</c>).</summary>
    internal bool IsAtCapacity => _candidates.Count >= _maxCells;

    /// <summary>
    /// The BEST-scoring candidate whose degradation reaches <paramref name="criticalRatio"/>, found without ranking (#949) — the same cell the ranked
    /// scan would have hoisted, chosen by an O(n) maximum instead of an O(n log n) sort.
    /// </summary>
    /// <remarks>
    /// <para><b>Best-scoring, not merely qualifying, and the difference was measured.</b> The first version returned whichever critical candidate the
    /// dictionary yielded first. The ranked path hoists the first critical cell in RANK order, which is the highest-scoring one, and
    /// <c>Score = degradation x tierWeight x clusterCount x ageFactor</c> — so an arbitrary pick can land on a cell with one cluster, which
    /// <c>RepairOneCell</c> declines outright (a partition of one cannot be improved) and which therefore spends the tick's single valve admission on
    /// nothing. On SWG Tatooine x16 that cost Creature 5 % of its repaired entities and 5 % of its units against the ranked arm, and turned a run-to-run
    /// spread of 0.7 entities into one of 5.8.</para>
    /// <para>Scoring every critical candidate is O(n) and the sort it replaces is O(n log n), so the saving this exists for survives: what is skipped is
    /// the ORDER over the whole queue, not the choice among the cells the valve may take.</para>
    /// </remarks>
    /// <remarks>With <c>skip</c> non-null, candidates of realms it does not run this tick are passed over (a non-runnable realm waits, D-6).</remarks>
    internal bool TryFindCritical(float criticalRatio, ArchetypeClusterState state, long tickNumber, out long key, RealmTable skip = null)
    {
        key = 0;
        if (criticalRatio <= 0f)
        {
            return false;
        }

        var found = false;
        var bestScore = 0f;
        foreach (var pair in _candidates)
        {
            // Copied out because Score takes its candidate by `in` and a KeyValuePair's Value is a property, so it has no referenceable location (CS8156).
            var candidate = pair.Value;
            if (candidate.Degradation < criticalRatio || (skip != null && !skip.IsRunnable(RealmOf(pair.Key))))
            {
                continue;
            }

            var score = Score(pair.Key, in candidate, state, tickNumber);
            if (!found || score > bestScore)
            {
                bestScore = score;
                key = pair.Key;
                found = true;
            }
        }

        return found;
    }

    /// <summary>The worst degradation nominated for a cooling cell since its repair, or <c>0</c> when it is not cooling or nothing nominated it.</summary>
    internal float HeldDegradationOf(long key) => _cooling.TryGetValue(key, out var held) ? held : 0f;

    /// <summary>
    /// Drop every candidate. Called when the archetype's cluster AABBs are rebuilt, because a candidate describes bounds that no longer exist.
    /// </summary>
    /// <remarks>
    /// A rebuild recomputes every cluster's bound from entity data and can reassign cell keys — which under the VDB grid are POOL SLOTS, not coordinates,
    /// so a key can name a different cell afterwards. A retained candidate would then rank a cell on a degradation that was measured elsewhere. Nothing
    /// corrupts (the planner re-reads the real bounds before it commits to a unit, and the no-op guard declines a cell that needs nothing), so this is a
    /// heuristic being kept honest rather than an invariant being enforced — but a queue whose inputs are all stale is a queue that ranks noise.
    /// </remarks>
    internal void Clear()
    {
        _candidates.Clear();
        _cooling.Clear();
        _coolingOrder.Clear();
        _rankedCount = 0;
        _dirtySinceRank = 0;
        _rankedTierVersion = -1;
    }
}
