import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';
import { GaugeId } from '@/libs/profiler/model/types';
import type { GaugeSeries, SpatialTickTelemetry } from '@/libs/profiler/model/traceModel';
import {
  archetypeIdsIn,
  checkDrifterIdentity,
  detectRepairPin,
  latestSampleFor,
  ratePerSecond,
  readTightness,
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
          {archetypeIds.map((id) => <option key={id} value={id}>#{id}</option>)}
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
            <FenceSpan gaugeSeries={gaugeData.gaugeSeries} migrationCpuMs={sample.row.migrationCpuMs} />
          </Group>

          <Group title="Structure" testId="spatial-group-structure" hint="What the partition looks like right now.">
            <Stat label="Active clusters" value={sample.row.activeClusters} />
            <Stat label="Cell-tree promotions" value={sample.row.cellTreePromotions} />
            <Stat label="Cell-tree demotions" value={sample.row.cellTreeDemotions} />
          </Group>

          <GridOccupancy gaugeSeries={gaugeData.gaugeSeries} />
        </>
      )}
    </div>
  );
}

// ── The three derived readings ───────────────────────────────────────────────────────────────────────────────────

function DerivedReadings({
  row, identity, repairPin,
}: {
  row: SpatialTickTelemetry;
  identity: ReturnType<typeof checkDrifterIdentity>;
  repairPin: ReturnType<typeof detectRepairPin>;
}) {
  const tightness = readTightness(row);

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
 */
function FenceSpan({ gaugeSeries, migrationCpuMs }: { gaugeSeries: Map<GaugeId, GaugeSeries>; migrationCpuMs: number }) {
  const series = gaugeSeries.get(GaugeId.ClusterFenceSpanUs);
  if (series === undefined || series.samples.length === 0) {
    return (
      <div className="flex items-baseline justify-between gap-2 text-fs-xs" data-testid="spatial-fence-span-absent">
        <span className="text-muted-foreground">Fence span</span>
        <span className="font-mono text-muted-foreground">serial fence — not measured</span>
      </div>
    );
  }

  const spanMs = series.samples[series.samples.length - 1].value / 1000;
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
