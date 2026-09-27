import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import { GaugeId } from '@/libs/profiler/model/types';
import type { GaugeSeries, SpatialTickTelemetry } from '@/libs/profiler/model/traceModel';
import {
  archetypeIdsIn,
  checkDrifterIdentity,
  detectRepairPin,
  latestSampleFor,
  ratePerSecond,
  readController,
  readQueryEfficiency,
  readRealmShapes,
  readTightness,
  realmRunStateName,
  windowGrowth,
} from './spatialReadings';

/**
 * Spatial Maintenance — the write/fence half of spatial observability (#911 O3).
 *
 * <b>Attach-only, and that is a data constraint rather than a preference.</b> Every counter here is per-tick, produced by the tick
 * fence and reset at the top of the next one, so it exists only while an engine is ticking. An `open` session loads a database file
 * with no tick loop and would render an entire panel of zeros that mean "there is no engine", not "the engine is quiet". The attach
 * transport is a one-way trace stream — there is no request/response channel to call `GetSpatialTelemetry` on the watched engine — so
 * the data arrives as two per-archetype instants the fence emits, projected onto `TickData.spatialByArchetype`.
 *
 * <b>Two clocks.</b> The per-tick figures are labelled with the tick they came from. Where a trend is more useful than an instant the
 * panel differentiates over the window instead of showing one arbitrary tick as if it were a rate.
 *
 * <b>Zero means zero.</b> No counter is ever rendered as "—" or "unknown". The one place the panel says something other than a number
 * is where the ENGINE makes a distinction: a tightness mean over zero samples is "no cluster was written", which is a fact about the
 * tick and not a missing measurement.
 */
export default function SpatialMaintenancePanel(_props: IDockviewPanelProps) {
  const sessionKind = useSessionStore((s) => s.kind);
  const sessionId = useSessionStore((s) => s.sessionId);
  const { windowedTicks, gaugeData, hasData } = useLiveGaugeData(sessionKind === 'attach' ? sessionId : null);

  const archetypeIds = useMemo(() => archetypeIdsIn(windowedTicks), [windowedTicks]);
  // Read the archetype table out of the store rather than through `useProfilerNameMaps`, which issues its own
  // TanStack query for metadata the Init SSE frame already delivered — an extra fetch for data in hand, and it drags
  // a QueryClientProvider into every test that renders this panel. Selecting `metadata?.archetypes` rather than
  // `metadata` matters: the DTO's identity flips on every live batch as tick summaries are appended, and subscribing
  // to the whole thing would re-render this panel at the batch rate for a table that never changes.
  const archetypes = useProfilerSessionStore((s) => s.metadata?.archetypes);
  const archetypeNames = useMemo(() => {
    const m = new Map<number, string>();
    for (const a of archetypes ?? []) {
      if (a.name) {
        m.set(Number(a.archetypeId), a.name);
      }
    }
    return m;
  }, [archetypes]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const archetypeId = selectedId !== null && archetypeIds.includes(selectedId) ? selectedId : archetypeIds[0] ?? null;

  if (sessionKind !== 'attach') {
    return (
      <ColdState>
        Spatial Maintenance is available in <b>Attach</b> sessions only. Its counters are produced by the tick fence and reset every
        tick, so they exist only while an engine is running. Open <i>Connect → Attach</i> and point it at a live engine.
      </ColdState>
    );
  }

  if (!hasData || archetypeId === null) {
    return (
      <ColdState>
        No spatial activity in the window yet. An engine with no <code>[SpatialIndex]</code> archetype never emits these counters; one
        that has them will fill this panel on its next fence.
      </ColdState>
    );
  }

  const sample = latestSampleFor(windowedTicks, archetypeId);
  const identity = checkDrifterIdentity(windowedTicks, archetypeId);
  const repairPin = detectRepairPin(windowedTicks, archetypeId);
  const migrationsPerSec = ratePerSecond(windowedTicks, archetypeId, (r) => r.migrations);
  const driftersPerSec = ratePerSecond(windowedTicks, archetypeId, (r) => r.driftersDetected);
  // #944 — the appended controller half. Efficiency is a WINDOW sum (the field's own instruction) while the controller's
  // state is read off the latest record: one is a cost over time, the other is where the controller stands right now.
  const efficiency = readQueryEfficiency(windowedTicks, archetypeId);
  const evictedInWindow = windowGrowth(windowedTicks, archetypeId, (r) => r.repairQueueEvicted);
  const realms = readRealmShapes(windowedTicks, archetypeId);
  const rebasesInWindow = windowGrowth(windowedTicks, archetypeId, (r) => r.efficiencyRebases);

  return (
    <div className="flex h-full w-full flex-col overflow-auto bg-background" data-testid="spatial-maintenance">
      <div className="flex items-center gap-3 border-b border-border px-3 py-2 text-fs-sm" data-testid="spatial-maintenance-header">
        <span className="text-muted-foreground">Archetype</span>
        <select
          className="rounded border border-border bg-background px-1 font-mono text-foreground"
          data-testid="spatial-maintenance-archetype"
          value={archetypeId}
          onChange={(e) => setSelectedId(Number(e.target.value))}
        >
          {/* Named, with the id kept beside it. The id alone is meaningless to whoever reads this panel, and the name is
              available in an attach session since the engine started pushing its archetype table over the Init frame
              (#WB-01) — `ProjectArchetypes` builds these from `reader.ArchetypeDefinitions`. Falls back to the bare id
              for an engine that sends no schema, which is the same session shape that hides the Schema Explorer. */}
          {archetypeIds.map((id) => (
            <option key={id} value={id}>{archetypeNames.get(id) ? `${archetypeNames.get(id)} (#${id})` : `#${id}`}</option>
          ))}
        </select>
        {/* The tick is named, never implied. A per-tick counter without the tick it came from is not a reading. */}
        <span className="ml-auto font-mono text-muted-foreground" data-testid="spatial-maintenance-tick">
          {sample === null ? 'no spatial tick in window' : `tick ${sample.tickNumber.toLocaleString()}`}
        </span>
      </div>

      {sample === null ? (
        <ColdState>This archetype produced no spatial record in the window.</ColdState>
      ) : (
        <>
          <DerivedReadings
            row={sample.row}
            identity={identity}
            repairPin={repairPin}
            efficiency={efficiency}
          />

          <Group title="Crossing" testId="spatial-group-crossing" hint="An entity left its cell. Correctness — never refused.">
            <Stat label="Migrations" value={sample.row.migrations} />
            <Stat label="Hysteresis absorbed" value={sample.row.hysteresisAbsorbed} />
            <Stat label="Crossings queued" value={sample.row.crossingsQueued} />
            <Stat label="Migrations/s (window)" value={migrationsPerSec} decimals={1} />
          </Group>

          <Group title="Relocation" testId="spatial-group-relocation" hint="Intra-cell drift. Quality — the budget may refuse it.">
            <Stat label="Drifters detected" value={sample.row.driftersDetected} />
            <Stat label="Admitted" value={sample.row.relocationsAdmitted} />
            <Stat label="Throttled" value={sample.row.relocationsThrottled} />
            <Stat label="Superseded" value={sample.row.relocationsSuperseded} />
            <Stat label="Unplaced" value={sample.row.driftersUnplaced} />
            <Stat label="— no candidate" value={sample.row.driftersUnplacedNoCandidate} />
            <Stat label="Spilled" value={sample.row.driftersSpilled} />
            <Stat label="Pins rejected" value={sample.row.pinsRejected} />
            <Stat label="Drifters/s (window)" value={driftersPerSec} decimals={1} />
          </Group>

          <Group title="Repair" testId="spatial-group-repair" hint="A cell's worst clusters, Morton re-sorted. Whole units only.">
            <Stat label="Units admitted" value={sample.row.repairUnits} />
            <Stat label="Units refused" value={sample.row.repairUnitsRefused} />
            <Stat label="Queue depth" value={sample.row.repairQueueDepth} hint="A level, not a rate — it persists across ticks." />
          </Group>

          <Group title="Budget" testId="spatial-group-budget" hint="What the fence spent, what it spent it on, and what the frame waited.">
            <Stat label="Budget committed" value={sample.row.budgetUsedMs} decimals={3} unit="ms" />
            {/* Labelled CPU-ms, deliberately. W workers busy for 1 ms report W — reading it as a duration is the error
                that made an 8 ms budget buy one repair unit. The span beside it is what makes this figure readable:
                their ratio is the parallelism the work achieved. */}
            <Stat label="Migration cost" value={sample.row.migrationCpuMs} decimals={3} unit="CPU-ms" hint="Summed across workers, not a span." />
            <FenceSpan gaugeSeries={gaugeData.gaugeSeries} migrationCpuMs={sample.row.migrationCpuMs} tickNumber={sample.tickNumber} />
          </Group>

          <Group
            title="Controller"
            testId="spatial-group-controller"
            hint="What the budget above was DERIVED from: the queries' efficiency decides the share of the configured budget the fence may spend."
          >
            <Stat label="Budget configured" value={sample.row.budgetConfiguredMs} decimals={3} unit="ms"
              hint="The ReclusterBudgetMs ceiling. Repeated in every record because an attach stream carries no configuration." />
            <Stat label="Budget granted" value={sample.row.budgetGrantedMs} decimals={3} unit="ms"
              hint="Configured x the share the efficiency earned. 'Budget committed' above is what the repair path then spent of it." />
            <Stat label="Tolerance" value={sample.row.efficiencyTolerance} decimals={2}
              hint="QueryEfficiencyTolerance. ZERO MEANS THE CONTROLLER IS OFF, not that it tolerates everything." />
            <Stat label="Candidates/hit (now)" value={sample.row.candidatesPerHitSmoothed} decimals={2} hint="Smoothed — the controller's input." />
            <Stat label="Candidates/hit (best)" value={sample.row.candidatesPerHitBest} decimals={2}
              hint="The set point: the lowest smoothed value since the last re-base." />
            <Stat label="Ticks at whole budget" value={sample.row.ticksAtWholeBudget}
              hint="Consecutive. The re-base comes the tick after EfficiencyRebaseTicks of them." />
            <Stat label="Re-bases (window)" value={rebasesInWindow}
              hint="Differentiated over the window — the raw counter is cumulative since the cluster state was created." />
            <Stat label="Measured cost" value={sample.row.measuredNsPerEntity} decimals={1} unit="ns/entity"
              hint="The per-entity migration cost the budget was actually spent against." />
            <Stat label="Drift-target boost" value={sample.row.driftTargetBoost} decimals={2} unit="x"
              hint="The throttle's multiplier on the drift target. 1 is none; at its cap, relocation detection is off." />
          </Group>

          <Group
            title="Repair health"
            testId="spatial-group-repair-health"
            hint="Whether the repair path is working or merely surviving: what cooled off, what the valve forced through, what fell off the queue."
          >
            <Stat label="Cells cooling" value={sample.row.repairCellsCooling}
              hint="Waiting out RepairCooldownTicks after a repair. A level, not a rate." />
            <Stat label="Valve fires" value={sample.row.repairValveFires}
              hint="Units admitted PAST the budget. A steady non-zero here with units pinned at one is the budget not working." />
            <Stat label="Entities repaired" value={sample.row.repairedEntities} />
            <Stat label="Queue evicted (window)" value={evictedInWindow}
              hint="Candidates dropped at the queue cap, differentiated over the window. Non-zero means repair demand exceeds the queue." />
          </Group>

          <Group title="Structure" testId="spatial-group-structure" hint="What the partition looks like right now.">
            <Stat label="Active clusters" value={sample.row.activeClusters} />
            <Stat label="Cell-tree promotions" value={sample.row.cellTreePromotions} />
            <Stat label="Cell-tree demotions" value={sample.row.cellTreeDemotions} />
          </Group>

          <Realms reading={realms} />

          <GridOccupancy gaugeSeries={gaugeData.gaugeSeries} />
        </>
      )}
    </div>
  );
}

// ── The three derived readings ───────────────────────────────────────────────────────────────────────────────────

function DerivedReadings({
  row, identity, repairPin, efficiency,
}: {
  row: SpatialTickTelemetry;
  identity: ReturnType<typeof checkDrifterIdentity>;
  repairPin: ReturnType<typeof detectRepairPin>;
  efficiency: ReturnType<typeof readQueryEfficiency>;
}) {
  const tightness = readTightness(row);
  const controller = readController(row);

  return (
    <div className="flex flex-col gap-2 border-b border-border p-3" data-testid="spatial-readings">
      {/* 1 — the identity, shown as a verdict rather than four numbers to subtract by hand. */}
      <Reading
        testId="spatial-reading-identity"
        title="Drifter identity"
        ok={identity === null ? null : identity.balanced}
        detail={identity === null
          ? 'No consecutive pair of ticks in the window carries this archetype — the identity spans two ticks and cannot be evaluated yet.'
          : `detected ${identity.detected.toLocaleString()} (tick ${identity.detectedTick}) `
            + `= admitted ${identity.admitted} + throttled ${identity.throttled} + superseded ${identity.superseded} `
            + `+ unplaced ${identity.unplaced} = ${identity.accountedFor.toLocaleString()} (tick ${identity.outcomeTick})`}
        note="Detection runs after Migrate, so a tick's drifters are decided by the NEXT tick's throttle."
      />

      {/* 2 — tightness against the bound, never against 1. */}
      <Reading
        testId="spatial-reading-tightness"
        title="Tightness to bound"
        ok={tightness.samples === 0 ? null : tightness.toBound <= 1.5}
        detail={tightness.samples === 0
          ? 'No cluster was written this tick, so nothing was measured. Not "clusters are points".'
          : `${tightness.toBound.toFixed(2)}× the bound — measured ${(tightness.extentRatio * 100).toFixed(1)}% of the cell `
            + `against a packing bound of ${(tightness.packingBound * 100).toFixed(1)}%, over ${tightness.samples.toLocaleString()} clusters`}
        note={tightness.inSingleClusterBasin
          ? 'The bound is the whole cell: this cell holds no more entities than one cluster, so intra-cell maintenance is correctly off.'
          : '1.00 is optimal packing. A Morton-ordered bulk load measures ~1.24; a random-order one ~1.56.'}
      />

      {/* 3 — the defect signature step 14 spent a campaign finding. */}
      <Reading
        testId="spatial-reading-repair-pin"
        title="Repair units per tick"
        ok={repairPin.ticksWithRepair === 0 ? null : !repairPin.pinned}
        detail={repairPin.ticksWithRepair === 0
          ? 'No repair unit was admitted in the window.'
          : `${repairPin.ticksPinnedAtOne} of ${repairPin.ticksWithRepair} repairing ticks admitted exactly one unit; `
            + `${repairPin.ticksWithRefusals} tick(s) refused a unit for budget`}
        note={repairPin.pinned
          ? 'Pinned at one while units are being refused — the signature of a budget the planner cannot spend, where the single unit each tick is the safety valve rather than the budget working.'
          : 'Watches for a unit count pinned at 1 across budgets while units are refused.'}
      />

      {/* 4 — #944: the controller, as a verdict. The nine numbers in the Controller block below are only readable against
          the one question they answer: is the fence being given the budget its query efficiency has earned? */}
      <Reading
        testId="spatial-reading-controller"
        title="Budget controller"
        ok={!controller.active || !controller.hasSignal ? null : controller.grantedShare >= 0.999 === controller.withinTolerance}
        detail={!controller.active
          ? 'QueryEfficiencyTolerance is 0 — the controller is off and the fence always receives the whole configured budget.'
          : !controller.hasSignal
            ? `No efficiency signal this tick (the queries did not hit enough to steer by), so the grant is being held at `
              + `${(controller.grantedShare * 100).toFixed(0)}% rather than decided.`
            : `granted ${(controller.grantedShare * 100).toFixed(0)}% of ${controller.configuredMs.toFixed(3)} ms — `
              + `candidates/hit ${controller.smoothed.toFixed(2)} against a best of ${controller.best.toFixed(2)} `
              + `(${controller.distanceFromBest.toFixed(2)}x, tolerance ${controller.tolerance.toFixed(2)})`}
        note={controller.rebasedThisTick
          ? 'This tick RE-BASED the best: the controller accepted the current cost as the new set point, having spent EfficiencyRebaseTicks at the whole budget without recovering the old one.'
          : `Window cost: ${efficiency.candidatesPerHit.toFixed(2)} candidates per hit over ${efficiency.samples.toLocaleString()} tick(s) — summed, never averaged.`}
      />
    </div>
  );
}

function Reading({ testId, title, ok, detail, note }: {
  testId: string; title: string; ok: boolean | null; detail: string; note: string;
}) {
  const badge = ok === null ? 'n/a' : ok ? 'ok' : 'check';
  const badgeClass = ok === null
    ? 'bg-muted text-muted-foreground'
    : ok ? 'bg-emerald-900/40 text-emerald-300' : 'bg-amber-900/40 text-amber-300';
  return (
    <div className="rounded border border-border p-2" data-testid={testId}>
      <div className="flex items-center gap-2 text-fs-sm">
        <span className={`rounded px-1.5 py-0.5 font-mono text-fs-xs ${badgeClass}`} data-testid={`${testId}-badge`}>{badge}</span>
        <span className="font-medium text-foreground">{title}</span>
      </div>
      <div className="mt-1 font-mono text-fs-xs text-foreground" data-testid={`${testId}-detail`}>{detail}</div>
      <div className="mt-0.5 text-fs-xs text-muted-foreground">{note}</div>
    </div>
  );
}

/**
 * The fence SPAN — the one figure that says what the partitioning cost the frame, as against every per-archetype timing
 * here, which is summed across workers. Engine-wide, so it arrives on the gauge channel rather than the archetype event.
 *
 * Absent series is a real state and is said out loud: a host that drives `WriteTickFence` itself never runs the
 * phase-exec systems that time the span, so there is nothing to report — which is not the same as a free fence.
 *
 * <b>The gauge sample is selected by tick, not by recency.</b> The span rides the engine-wide gauge channel and the CPU-ms
 * comes from the archetype's own record, so "newest of each" is two different ticks whenever the archetype has been quiet at
 * the end of the window — and their ratio is then a number about nothing. The panel's rule is that the tick is named, never
 * implied; a span shown under a header labelled with a row's tick has to BE that tick's span.
 */
function FenceSpan(
  { gaugeSeries, migrationCpuMs, tickNumber }:
  { gaugeSeries: Map<GaugeId, GaugeSeries>; migrationCpuMs: number; tickNumber: number },
) {
  const series = gaugeSeries.get(GaugeId.ClusterFenceSpanUs);
  if (series === undefined || series.samples.length === 0) {
    return (
      <div className="flex items-baseline justify-between gap-2 text-fs-xs" data-testid="spatial-fence-span-absent">
        <span className="text-muted-foreground">Fence span</span>
        <span className="font-mono text-muted-foreground">serial fence — not measured</span>
      </div>
    );
  }

  // "The series exists but not for THIS tick" is a third state, and collapsing it into either of the other two lies. Showing the
  // newest sample would pair a span from one tick with CPU-ms from another under a header naming the second; showing "not
  // measured" would claim a serial fence on an engine that plainly has a parallel one.
  const matching = series.samples.find((s) => s.tickNumber === tickNumber);
  if (matching === undefined) {
    return (
      <div className="flex items-baseline justify-between gap-2 text-fs-xs" data-testid="spatial-fence-span-tick-mismatch">
        <span className="text-muted-foreground">Fence span</span>
        <span className="font-mono text-muted-foreground">no span recorded for tick {tickNumber.toLocaleString()}</span>
      </div>
    );
  }

  const spanMs = matching.value / 1000;
  // Parallelism, shown only when both terms are real: CPU-ms over span is how many workers' worth of CPU one unit of
  // span bought, and it is the whole reason the two are printed together.
  const parallelism = spanMs > 0 && migrationCpuMs > 0 ? migrationCpuMs / spanMs : 0;
  return (
    <Stat
      label="Fence span"
      value={spanMs}
      decimals={3}
      unit="ms"
      hint={parallelism > 0 ? `migration CPU / span = ${parallelism.toFixed(1)}x` : 'What the host waited for the fence.'}
    />
  );
}

// ── Grid occupancy — engine-wide, so it rides the gauge channel ──────────────────────────────────────────────────

function GridOccupancy({ gaugeSeries }: { gaugeSeries: Map<GaugeId, GaugeSeries> }) {
  // The newest sample, not a mean: occupancy is a level. `SpatialGridBlockCount` is also the "is there a grid at all"
  // test — the emitter skips the whole group rather than sending five zero series when there is none.
  const last = (id: GaugeId): number | null => {
    const s = gaugeSeries.get(id);
    return s === undefined || s.samples.length === 0 ? null : s.samples[s.samples.length - 1].value;
  };

  const blocks = last(GaugeId.SpatialGridBlockCount);
  if (blocks === null) {
    return (
      <Group title="Grid occupancy" testId="spatial-group-grid" hint="Engine-wide — one grid serves every spatial archetype.">
        <div className="col-span-full text-fs-xs text-muted-foreground">
          This engine has no spatial grid, so it emits no occupancy series.
        </div>
      </Group>
    );
  }

  const resident = last(GaugeId.SpatialGridResidentBytes) ?? 0;
  const dense = last(GaugeId.SpatialGridDenseEquivalentBytes) ?? 0;
  return (
    <Group title="Grid occupancy" testId="spatial-group-grid" hint="Engine-wide — one grid serves every spatial archetype.">
      <Stat label="Blocks" value={blocks} />
      <Stat label="Occupied cells" value={last(GaugeId.SpatialGridOccupiedCells) ?? 0} />
      {/* The gauge carries hundredths of a percent — see GaugeValueKind.U32PercentHundredths. */}
      <Stat label="Intra-block fill" value={(last(GaugeId.SpatialGridIntraBlockFill) ?? 0) / 100} decimals={1} unit="%" />
      <Stat label="Resident" value={resident} unit="B" />
      {/* Resident against dense IS the sparse grid's argument — and the guard on VG-02: a read path that creates cells
          makes resident climb toward dense with no other symptom. */}
      <Stat label="Dense equivalent" value={dense} unit="B" hint={dense > 0 ? `${(resident / dense * 100).toFixed(1)}% of dense` : undefined} />
    </Group>
  );
}

// ── Layout primitives ────────────────────────────────────────────────────────────────────────────────────────────

/**
 * The per-realm shape table (#WB-05, kind 67).
 *
 * <b>Shape, not rates, and the block says so.</b> A realm owns its grid, its cell size and its maintenance budget since
 * #1050, so "is spatial healthy" became a per-realm question — a thrashing 50 m dungeon is invisible behind a calm 16 km
 * planet. What is per-realm in the engine TODAY is the shape; the per-tick counters are owned per archetype, one set for
 * every realm, so this table cannot show them and the footer says which figures are summed instead of leaving a reader
 * to assume the rows account for everything.
 *
 * <b>Runnable realms only.</b> A dormant realm sends no row, which is why the census is printed beside the count: "3 of
 * 1 188" is a fact about the engine, while four rows with no denominator reads as four realms existing.
 */
function Realms({ reading }: { reading: ReturnType<typeof readRealmShapes> }) {
  if (reading.tickNumber === null) {
    return (
      <div className="border-b border-border p-3" data-testid="spatial-group-realms">
        <div className="text-fs-sm font-medium text-foreground">Realms</div>
        <div className="mt-1 text-fs-xs text-muted-foreground">
          No realm rows in the window. An engine with one realm still emits a row for it, so this means the per-realm
          telemetry flag is off (<code>Spatial:ClusterMigration:RealmTelemetry</code>) or no realm was runnable.
        </div>
      </div>
    );
  }

  const hidden = Math.max(0, reading.presentRealms - reading.rows.length);
  return (
    <div className="border-b border-border p-3" data-testid="spatial-group-realms">
      <div className="text-fs-sm font-medium text-foreground">Realms</div>
      <div className="mb-2 text-fs-xs text-muted-foreground" data-testid="spatial-realms-census">
        {`${reading.rows.length.toLocaleString()} runnable of ${reading.presentRealms.toLocaleString()} this archetype lives in`}
        {hidden > 0 ? ` — ${hidden.toLocaleString()} not runnable, so not shown` : ''}
        {` (tick ${reading.tickNumber.toLocaleString()})`}
      </div>
      <table className="w-full text-fs-xs" data-testid="spatial-realms-table">
        <thead className="text-muted-foreground">
          <tr className="text-left">
            <th className="font-normal">realm</th>
            <th className="font-normal">state</th>
            <th className="font-normal text-right">÷</th>
            <th className="font-normal text-right">cell</th>
            <th className="font-normal text-right">cells</th>
            <th className="font-normal text-right">clusters</th>
            <th className="font-normal text-right">reach</th>
            <th className="font-normal text-right">escaped</th>
            <th className="font-normal text-right">promoted</th>
            <th className="font-normal text-right">blocked</th>
            {/* "declared", not "budget". Maintenance is budgeted per archetype from realm 0's grid, so a realm's configured
                value is what it asks for and not what it gets — a column reading "budget" would be presenting the ceiling a
                grant was measured against, which is a different number and lives on the archetype row. */}
            <th className="font-normal text-right">budget&nbsp;(decl)</th>
          </tr>
        </thead>
        <tbody className="font-mono text-foreground">
          {reading.rows.map((r) => (
            <tr key={r.realmId} data-testid={`spatial-realm-row-${r.realmId}`}>
              <td>#{r.realmId}</td>
              <td className="font-sans text-muted-foreground">{realmRunStateName(r.runState)}</td>
              <td className="text-right">{r.divisor}</td>
              <td className="text-right">{r.cellSize.toLocaleString(undefined, { maximumFractionDigits: 1 })} m</td>
              <td className="text-right">{r.cellCount.toLocaleString()}{r.gridDepth > 1 ? ' ³' : ''}</td>
              <td className="text-right">{r.clusters.toLocaleString()}</td>
              {/* Reach in CELLS, with the metres beside it. The raw value is not a reading: 180 m is nothing in a 1 km
                  realm and means the broadphase has stopped pruning in a 64 m one. */}
              <td className={`text-right ${r.reachBlown ? 'text-amber-300' : ''}`} title={`${r.clusterReach.toFixed(1)} m`}>
                {r.reachInCells.toLocaleString(undefined, { maximumFractionDigits: 2 })}×
              </td>
              <td className="text-right">{r.escapedClusters.toLocaleString()}</td>
              <td className="text-right">{r.promotedCells.toLocaleString()}</td>
              <td className="text-right">{r.blockedCells.toLocaleString()}</td>
              <td
                className={`text-right ${Math.abs(r.budgetConfiguredMs - reading.enforcedBudgetMs) > 0.01 ? 'text-amber-300' : ''}`}
                title={`declares ${r.budgetConfiguredMs.toFixed(2)} ms; the engine enforces ${reading.enforcedBudgetMs.toFixed(2)} ms for the whole archetype`}
              >
                {r.budgetConfiguredMs.toFixed(2)} ms
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="mt-2 text-fs-xs text-muted-foreground" data-testid="spatial-realms-footer">
        Shape only. Migrations, repair units, budget spent and the tightness means are owned per archetype in the engine —
        one set of counters for every realm — so the blocks above are sums across these rows, not one realm's work.
        {' '}
        <b>budget (decl)</b> is each realm&apos;s declaration; the engine enforces{' '}
        <span className="font-mono">{reading.enforcedBudgetMs.toFixed(2)} ms</span> for the whole archetype, from realm 0&apos;s
        grid.
        {reading.someRealmDeclaresADifferentBudget
          ? ' A realm highlighted above declares a budget that is not the one being enforced for it.'
          : ''}
      </div>
    </div>
  );
}

function Group({ title, testId, hint, children }: {
  title: string; testId: string; hint: string; children: React.ReactNode;
}) {
  return (
    <div className="border-b border-border p-3" data-testid={testId}>
      <div className="text-fs-sm font-medium text-foreground">{title}</div>
      <div className="mb-2 text-fs-xs text-muted-foreground">{hint}</div>
      <div className="grid grid-cols-2 gap-x-4 gap-y-1 md:grid-cols-3">{children}</div>
    </div>
  );
}

function Stat({ label, value, decimals = 0, unit, hint }: {
  label: string; value: number; decimals?: number; unit?: string; hint?: string;
}) {
  return (
    <div className="flex items-baseline justify-between gap-2 text-fs-xs" title={hint}>
      <span className="text-muted-foreground">{label}</span>
      <span className="font-mono text-foreground">
        {value.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })}
        {unit ? ` ${unit}` : ''}
      </span>
    </div>
  );
}

function ColdState({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center" data-testid="spatial-maintenance-cold">
      <div className="max-w-md text-fs-base text-muted-foreground">{children}</div>
    </div>
  );
}
