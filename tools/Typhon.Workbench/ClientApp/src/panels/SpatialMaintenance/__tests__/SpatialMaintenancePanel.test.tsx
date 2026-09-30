// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { SpatialRealmShape, SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { realmArchetypeKey } from '@/libs/profiler/model/traceModel';
import { GaugeId } from '@/libs/profiler/model/types';
import SpatialMaintenancePanel from '../SpatialMaintenancePanel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import type { ProfilerMetadataDto } from '@/api/generated/model';

// The panel's only data source. Stubbed rather than driven through the chunk cache: this fixture is about what the panel
// SHOWS for a given set of counters, and building a real decoded trace to reach three numbers would test the decoder.
const live = vi.hoisted(() => ({ data: null as unknown }));
vi.mock('@/hooks/profiler/useLiveGaugeData', () => ({
  useLiveGaugeData: () => live.data,
}));

const NO_PROPS = {} as IDockviewPanelProps;
const ARCH = 2;

function row(over: Partial<SpatialTickTelemetry> = {}): SpatialTickTelemetry {
  return {
    archetypeId: ARCH,
    migrations: 0, hysteresisAbsorbed: 0, migrationCpuMs: 0,
    driftersDetected: 0, relocationsAdmitted: 0, relocationsThrottled: 0, relocationsSuperseded: 0,
    driftersUnplaced: 0, driftersUnplacedNoCandidate: 0, driftersSpilled: 0, pinsRejected: 0, crossingsQueued: 0,
    repairUnits: 0, repairUnitsRefused: 0, repairQueueDepth: 0,
    budgetUsedMs: 0,
    tightnessSamples: 0, extentRatio: 0, packingBound: 0,
    activeClusters: 0, cellTreePromotions: 0, cellTreeDemotions: 0,
    queryClustersOpened: 0, queryCandidates: 0, queryHits: 0,
    budgetConfiguredMs: 0, budgetGrantedMs: 0, efficiencyTolerance: 0,
    candidatesPerHitSmoothed: 0, candidatesPerHitBest: 0, ticksAtWholeBudget: 0,
    controllerFlags: 0, efficiencyRebases: 0,
    repairCellsCooling: 0, repairValveFires: 0, repairedEntities: 0, repairQueueEvicted: 0,
    measuredNsPerEntity: 0, driftTargetBoost: 0,
    presentRealms: 0, runnableRealms: 0, ratesRealmsTouched: 0, ratesRealmsEmitted: 0,
    ...over,
  };
}

function tick(tickNumber: number, rows: SpatialTickTelemetry[]): TickData {
  return {
    tickNumber,
    startUs: tickNumber * 1000,
    endUs: tickNumber * 1000 + 1000,
    spatialByArchetype: new Map(rows.map((r) => [r.archetypeId, r])),
  } as unknown as TickData;
}

/** A tick that also carries per-realm rows (#WB-05, kind 67), with the census on its archetype row. */
function tickWithRealms(
  tickNumber: number, archetypeRow: SpatialTickTelemetry, shapes: Array<Partial<SpatialRealmShape>>,
): TickData {
  const rows = shapes.map((over) => ({
    realmId: 0, archetypeId: ARCH, runState: 2, divisor: 1,
    cellSize: 1024, cellCount: 256, gridDepth: 1, clusters: 31,
    clusterReach: 12.4, escapedClusters: 0, promotedCells: 0, blockedCells: 0,
    budgetConfiguredMs: 8, efficiencyTolerance: 0.25,
    ...over,
  } satisfies SpatialRealmShape));

  return {
    tickNumber,
    startUs: tickNumber * 1000,
    endUs: tickNumber * 1000 + 1000,
    spatialByArchetype: new Map([[ARCH, archetypeRow]]),
    spatialByRealm: new Map(rows.map((r) => [realmArchetypeKey(r.realmId, r.archetypeId), r])),
  } as unknown as TickData;
}

function setLive(ticks: TickData[], gauges: Array<[GaugeId, number]> = [], gaugeTick = 1) {
  live.data = {
    windowedTicks: ticks,
    gaugeData: {
      gaugeSeries: new Map(gauges.map(([id, value]) => [id, { id, samples: [{ tickNumber: gaugeTick, timestampUs: 0, value }] }])),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [], gcEvents: [], gcSuspensions: [], offCpuBySlot: new Map(),
    },
    windowStartUs: 0,
    windowEndUs: 1000,
    hasData: ticks.length > 0,
  };
}

beforeEach(() => {
  useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
  setLive([]);
});
afterEach(() => cleanup());

describe('Spatial Maintenance panel (#911 O3)', () => {
  it('renders an explained cold state outside an attach session', () => {
    // Not a preference: every counter here is per-tick and reset by the fence, so it exists only while an engine ticks.
    // An open session would render a whole panel of zeros meaning "there is no engine".
    useSessionStore.setState({ kind: 'open', sessionId: 'sess-T', filePath: '/db.typhon' });
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const cold = screen.getByTestId('spatial-maintenance-cold');
    expect(cold.textContent).toContain('Attach');
    expect(screen.queryByTestId('spatial-maintenance-header')).toBeNull();
  });

  it('says so when the window carries no spatial activity, rather than showing zeros', () => {
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-maintenance-cold').textContent).toContain('No spatial activity');
  });

  it('names the tick its per-tick figures came from', () => {
    // Two clocks: these are per-tick values, so a panel polling at UI rate is showing one arbitrary tick out of hundreds.
    // Showing the number without the tick is not a reading.
    setLive([tick(41, [row({ migrations: 3 })]), tick(42, [row({ migrations: 12 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-maintenance-tick').textContent).toContain('42');
  });

  it('groups the counters by mechanism', () => {
    setLive([tick(1, [row({ migrations: 5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    for (const id of ['crossing', 'relocation', 'repair', 'budget', 'controller', 'repair-health', 'structure', 'realms', 'grid']) {
      expect(screen.getByTestId(`spatial-group-${id}`)).toBeTruthy();
    }
  });

  // ── #944: the 17 fields the decoder used to drop ────────────────────────────────────────────────────────────────

  it('renders the controller block from the appended fields, budget arithmetic included', () => {
    setLive([tick(1, [row({
      budgetConfiguredMs: 8, budgetGrantedMs: 2, efficiencyTolerance: 0.25,
      candidatesPerHitSmoothed: 2.5, candidatesPerHitBest: 2, ticksAtWholeBudget: 13,
      measuredNsPerEntity: 145.5, driftTargetBoost: 1.5,
    })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const controller = screen.getByTestId('spatial-group-controller').textContent ?? '';
    // Configured AND granted, both, because the whole point is the gap between them: the Budget block above shows what
    // was spent, and without the ceiling beside the grant a small number reads as a small workload.
    expect(controller).toContain('8.000');
    expect(controller).toContain('2.000');
    expect(controller).toContain('13');
    expect(controller).toContain('145.5');
  });

  it('says the controller is OFF at zero tolerance instead of drawing its figures as measurements', () => {
    setLive([tick(1, [row({ efficiencyTolerance: 0, budgetConfiguredMs: 8, budgetGrantedMs: 8 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    expect(screen.getByTestId('spatial-reading-controller-detail').textContent).toContain('controller is off');
    // n/a, not ok: there is no verdict to give about a controller that is not running.
    expect(screen.getByTestId('spatial-reading-controller-badge').textContent).toBe('n/a');
  });

  it('holds the verdict when the queries gave no signal, rather than reporting the held grant as a decision', () => {
    setLive([tick(1, [row({
      efficiencyTolerance: 0.25, budgetConfiguredMs: 8, budgetGrantedMs: 4, controllerFlags: 0,
      candidatesPerHitSmoothed: 2.5, candidatesPerHitBest: 2,
    })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const detail = screen.getByTestId('spatial-reading-controller-detail').textContent ?? '';
    expect(detail).toContain('No efficiency signal');
    expect(detail).toContain('50%');
    expect(screen.getByTestId('spatial-reading-controller-badge').textContent).toBe('n/a');
  });

  it('flags a grant that disagrees with its own tolerance', () => {
    // Inside tolerance (1.25x against 0.25) but granted a quarter of the budget. The two cannot both be right, and the
    // reading says check rather than presenting a plausible-looking share.
    setLive([tick(1, [row({
      efficiencyTolerance: 0.25, budgetConfiguredMs: 8, budgetGrantedMs: 2, controllerFlags: 0x01,
      candidatesPerHitSmoothed: 2.5, candidatesPerHitBest: 2,
    })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    expect(screen.getByTestId('spatial-reading-controller-badge').textContent).toBe('check');
    expect(screen.getByTestId('spatial-reading-controller-detail').textContent).toContain('1.25x');
  });

  it('reports a re-base in the note, where it is the one event worth reading over the window cost', () => {
    setLive([tick(1, [row({ efficiencyTolerance: 0.25, controllerFlags: 0x03, candidatesPerHitBest: 2, candidatesPerHitSmoothed: 2 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-controller').textContent).toContain('RE-BASED');
  });

  it('lists one row per runnable realm and states how many it is NOT showing', () => {
    setLive([tickWithRealms(41_012, row({ presentRealms: 1188, runnableRealms: 2 }), [
      { realmId: 0, cellSize: 1024, clusterReach: 12.4, promotedCells: 31 },
      { realmId: 7, cellSize: 64, clusterReach: 180.9, escapedClusters: 3, promotedCells: 2, blockedCells: 4 },
    ])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    expect(screen.getByTestId('spatial-realm-row-0')).toBeTruthy();
    expect(screen.getByTestId('spatial-realm-row-7')).toBeTruthy();
    const census = screen.getByTestId('spatial-realms-census').textContent ?? '';
    // Two rows out of 1,188 present: without the denominator the table reads as "this archetype lives in two realms".
    expect(census).toContain('1,188');
    expect(census).toContain('1,186 not runnable');
    expect(census).toContain('41,012');
  });

  it('shows reach in cells, so the same metres read differently in a planet and a dungeon', () => {
    setLive([tickWithRealms(1, row({ presentRealms: 2, runnableRealms: 2 }), [
      { realmId: 0, cellSize: 1024, clusterReach: 180.9 },
      { realmId: 7, cellSize: 64, clusterReach: 180.9 },
    ])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    // Identical reach in metres; 0.18x of a cell against 2.83x of one.
    expect(screen.getByTestId('spatial-realm-row-0').textContent).toContain('0.18×');
    expect(screen.getByTestId('spatial-realm-row-7').textContent).toContain('2.83×');
  });

  it('names each realm run state rather than printing the enum value', () => {
    setLive([tickWithRealms(1, row({ presentRealms: 2, runnableRealms: 2 }), [
      { realmId: 0, runState: 2 },
      { realmId: 7, runState: 1, divisor: 4 },
    ])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    expect(screen.getByTestId('spatial-realm-row-0').textContent).toContain('Active');
    expect(screen.getByTestId('spatial-realm-row-7').textContent).toContain('Simulated');
  });

  it('says why the realm table is empty instead of rendering a table with no rows', () => {
    setLive([tick(1, [row({ migrations: 5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-realms').textContent).toContain('RealmTelemetry');
  });

  it('states that the counter blocks are sums across the realm rows, not one realm work', () => {
    // The panel must not let a reader take the Crossing/Repair/Budget blocks for the selected realm's work: the engine
    // owns those counters per archetype, one set for every realm.
    setLive([tickWithRealms(1, row({ presentRealms: 2, runnableRealms: 2 }), [{ realmId: 0 }, { realmId: 7 }])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-realms').textContent).toContain('owned per archetype');
  });

  it('labels the realm budget as DECLARED and names the one the engine enforces', () => {
    setLive([tickWithRealms(1, row({ presentRealms: 2, runnableRealms: 2, budgetConfiguredMs: 8 }), [
      { realmId: 0, budgetConfiguredMs: 8 },
      { realmId: 7, budgetConfiguredMs: 4 },
    ])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const realms = screen.getByTestId('spatial-group-realms').textContent ?? '';
    // The column cannot read "budget": one budget is enforced for the whole archetype, from realm 0's grid, and a row's
    // own value is what that realm asks for.
    expect(realms).toContain('decl');
    const footer = screen.getByTestId('spatial-realms-footer').textContent ?? '';
    expect(footer).toContain('8.00 ms');
    expect(footer).toContain("realm 0");
    // Realm 7 declares 4 ms and will not get it — the footer says a row is highlighted rather than leaving the colour to
    // be guessed at.
    expect(footer).toContain('declares a budget that is not the one being enforced');
  });

  it('says nothing about a mismatch when every realm declares what is enforced', () => {
    setLive([tickWithRealms(1, row({ presentRealms: 2, runnableRealms: 2, budgetConfiguredMs: 8 }), [
      { realmId: 0, budgetConfiguredMs: 8 },
      { realmId: 7, budgetConfiguredMs: 8 },
    ])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const footer = screen.getByTestId('spatial-realms-footer').textContent ?? '';
    expect(footer).toContain('8.00 ms');
    expect(footer).not.toContain('declares a budget that is not');
  });

  it('differentiates the cumulative repair-queue evictions instead of showing a lifetime total', () => {
    setLive([
      tick(1, [row({ repairQueueEvicted: 1_000_000, repairCellsCooling: 21, repairValveFires: 2, repairedEntities: 512 })]),
      tick(2, [row({ repairQueueEvicted: 1_000_004, repairCellsCooling: 21, repairValveFires: 2, repairedEntities: 512 })]),
    ]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const health = screen.getByTestId('spatial-group-repair-health').textContent ?? '';
    expect(health).toContain('21');
    expect(health).toContain('512');
    // 4 in the window, not 1,000,004 — the raw counter is cumulative since the cluster state was created.
    expect(health).toContain('4');
    expect(health).not.toContain('1,000,004');
  });

  it('shows the drifter identity as a verdict, over the one-tick lag', () => {
    setLive([
      tick(1, [row({ driftersDetected: 6 })]),
      tick(2, [row({ relocationsAdmitted: 2, relocationsThrottled: 3, driftersUnplaced: 1 })]),
    ]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-identity-badge').textContent).toBe('ok');
    expect(screen.getByTestId('spatial-reading-identity-detail').textContent).toContain('tick 1');
    expect(screen.getByTestId('spatial-reading-identity-detail').textContent).toContain('tick 2');
  });

  it('reads tightness against the bound, and says "nothing was written" rather than implying points', () => {
    setLive([tick(1, [row({ tightnessSamples: 0 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const detail = screen.getByTestId('spatial-reading-tightness-detail').textContent ?? '';
    expect(detail).toContain('No cluster was written');
    expect(detail).not.toContain('0.00×');
  });

  it('reports the ratio to the bound when clusters were sampled', () => {
    setLive([tick(1, [row({ tightnessSamples: 10, extentRatio: 0.9, packingBound: 0.5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-tightness-detail').textContent).toContain('1.80×');
  });

  it('labels the migration cost as CPU-ms, never as a duration', () => {
    // W workers busy for 1 ms report W. Rendering it as a duration is the error that made an 8 ms budget buy one unit.
    setLive([tick(1, [row({ migrationCpuMs: 4.5 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-budget').textContent).toContain('CPU-ms');
  });

  it('shows the fence span beside the CPU-ms, with the parallelism their ratio implies', () => {
    // The pairing IS the point: summed CPU cannot be compared to a frame budget on its own, and the span is what the
    // host actually waited. 9000 us = 9 ms span against 36 CPU-ms is 4x.
    setLive([tick(1, [row({ migrationCpuMs: 36 })])], [[GaugeId.ClusterFenceSpanUs, 9000]]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const budget = screen.getByTestId('spatial-group-budget');
    expect(budget.textContent).toContain('9.000 ms');
    expect(budget.textContent).toContain('CPU-ms');
    expect(screen.queryByTestId('spatial-fence-span-absent')).toBeNull();
  });

  it('says the fence ran serially rather than printing a zero duration', () => {
    // A host driving WriteTickFence itself never runs the phase-exec systems that time the span, so the series is absent.
    // Rendering that as "0.000 ms" would claim a free fence, which is the one reading the engine cannot support.
    setLive([tick(1, [row({ migrationCpuMs: 36, budgetUsedMs: 2 })])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const absent = screen.getByTestId('spatial-fence-span-absent');
    expect(absent.textContent).toContain('serial fence');
    // Scoped to the span's own row: the group legitimately prints durations for the other stats, so asserting over the
    // whole group would pass or fail on those instead of on the thing under test.
    expect(absent.textContent).not.toMatch(/\d\s*ms/);
  });

  it('flags a repair unit count pinned at one while units are refused', () => {
    setLive(Array.from({ length: 6 }, (_, i) => tick(i + 1, [row({ repairUnits: 1, repairUnitsRefused: 2 })])));
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-reading-repair-pin-badge').textContent).toBe('check');
  });

  it('will not pair a span from one tick with CPU-ms from another', () => {
    // The span rides the engine-wide gauge channel and the CPU-ms comes from the archetype's own record, so "newest of each" is
    // two different ticks the moment the archetype goes quiet at the end of the window. Their ratio would then be a number about
    // nothing, printed under a header naming only one of them.
    setLive([tick(7, [row({ migrationCpuMs: 36 })])], [[GaugeId.ClusterFenceSpanUs, 9000]], 5);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);

    const mismatch = screen.getByTestId('spatial-fence-span-tick-mismatch');
    expect(mismatch.textContent).toContain('7');
    expect(mismatch.textContent).not.toContain('9.000');
    // And it must not claim a serial fence either — the engine has a span, just not for this tick.
    expect(screen.queryByTestId('spatial-fence-span-absent')).toBeNull();
  });

  it('says an engine with no grid emits no occupancy series, rather than drawing zeros', () => {
    setLive([tick(1, [row()])]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    expect(screen.getByTestId('spatial-group-grid').textContent).toContain('no spatial grid');
  });

  it('shows grid occupancy when the gauge series is present', () => {
    setLive([tick(1, [row()])], [
      [GaugeId.SpatialGridBlockCount, 12],
      [GaugeId.SpatialGridOccupiedCells, 3400],
      [GaugeId.SpatialGridIntraBlockFill, 4250],
      [GaugeId.SpatialGridResidentBytes, 262_144],
      [GaugeId.SpatialGridDenseEquivalentBytes, 134_217_728],
    ]);
    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const text = screen.getByTestId('spatial-group-grid').textContent ?? '';
    // The gauge carries hundredths of a percent — 4250 is 42.5 %, and rendering it as 4,250 % is the mistake to catch.
    expect(text).toContain('42.5 %');
    expect(text).not.toContain('no spatial grid');
  });
});

describe('Spatial Maintenance panel — the archetype selector is named, not numbered', () => {
  // Reported from a live session: the combo showed "#1", "#2". The id alone is meaningless to whoever reads this panel,
  // and the name has been available in an attach session since the engine started pushing its archetype table over the
  // Init frame (#WB-01) — `AttachSessionRuntime.ProjectArchetypes` builds it from `reader.ArchetypeDefinitions`. The
  // panel simply never looked it up.

  it('labels each option with the archetype name and keeps the id beside it', () => {
    useProfilerSessionStore.setState({
      metadata: {
        archetypes: [
          { archetypeId: 1, name: 'Swg.Creatures' },
          { archetypeId: 2, name: 'Swg.Players' },
        ],
      } as unknown as ProfilerMetadataDto,
    });
    setLive([tick(1, [row({ archetypeId: 1 }), row({ archetypeId: 2 })])]);

    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const select = screen.getByTestId('spatial-maintenance-archetype');
    const labels = Array.from(select.querySelectorAll('option')).map((o) => o.textContent);

    expect(labels).toContain('Swg.Creatures (#1)');
    expect(labels).toContain('Swg.Players (#2)');
  });

  it('falls back to the bare id for an engine that sends no archetype table', () => {
    // The same session shape that hides the Schema Explorer: an older engine, or a schema too large for one Init
    // frame. A bare id is honest there; inventing a name would not be.
    useProfilerSessionStore.setState({ metadata: { archetypes: [] } as unknown as ProfilerMetadataDto });
    setLive([tick(1, [row({ archetypeId: 7 })])]);

    render(<SpatialMaintenancePanel {...NO_PROPS} />);
    const select = screen.getByTestId('spatial-maintenance-archetype');
    expect(Array.from(select.querySelectorAll('option')).map((o) => o.textContent)).toEqual(['#7']);
  });
});
