import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { type PhaseAxis, tickOffsetToNormalizedX } from './phaseLayout';
import { resolveEffectiveLevel, type Track } from './trackBuilding';
import type { GranularityLevel } from './useDataFlowViewStore';

/**
 * One bar = one (system, archetype, tick) datum projected onto a single track row. The X axis is tick number;
 * the timeline component maps that to pixel space using the {@link computePhaseLayout} segments. Color encodes
 * the system's primary access kind on the row's component (read / write / fresh / snapshot) and is resolved at
 * render time from the System's `reads/writes/...` arrays.
 *
 * Phase B v1 builds bars only — interaction state (hover / select) is layered on top by the uPlot wrapper.
 */
export interface Bar {
  /** Track id this bar lands on. Matches `Track.id` produced by `buildTracks`. */
  readonly trackId: string;
  /** Tick number at which the bar is centered (engine-side `SchedulerSystemArchetypeEvent.tickNumber`). */
  readonly tickNumber: number;
  /**
   * X start in normalized [0, 1] phase-space. When a {@link PhaseAxis} is supplied, this is the bar's
   * intra-phase fraction mapped through the phase segment's [xStart, xEnd]. Without phase data, the
   * caller falls back to a single virtual segment covering the whole timeline (xStart in [0, 1] equals
   * the bar's tick fraction inside the displayed range).
   */
  readonly xStart: number;
  /** X end in normalized [0, 1] phase-space. Same projection as {@link xStart}. */
  readonly xEnd: number;
  /** Phase name the bar belongs to — empty string when the system declares no phase (legacy traces). */
  readonly phaseName: string;
  /** Underlying system name — used for cross-panel selection mirror + hover-to-isolate matching. */
  readonly systemName: string;
  /** Archetype id from the source event. Carried so the side panel can render full detail. */
  readonly archetypeId: number;
  /** Entity count from the source event. Drives bar height/intensity at finer granularities. */
  readonly entityCount: number;
  /** Chunk count from the source event. */
  readonly chunkCount: number;
}

/**
 * Build bars for the timeline at the requested granularity. Pure function; cheap enough to run on every
 * (touches, level, topology) change. The fan-out cost is O(touches × meanComponentsPerArchetype) at L3/L4 and
 * O(touches) at L0–L2. For typical workloads (few thousand touches × 4–6 components) this stays well under
 * a millisecond.
 *
 * Empty inputs return `[]` — the timeline renders an empty canvas without erroring.
 *
 * @param touches Sliced (already range-filtered) row array — see `findTickRangeSlice`.
 * @param tracks   Output of `buildTracks(topology, level)` — used to fast-skip when no track of the relevant kind exists.
 * @param topology Full topology for component-name / archetype-id lookups.
 * @param level    Granularity altitude — controls the fan-out strategy.
 */
export function buildBars(
  touches: readonly SystemArchetypeTouchSummary[],
  tracks: readonly Track[],
  topology: TopologyDto | null,
  level: GranularityLevel,
  // Optional precision inputs — when provided, bars are positioned at sub-tick granularity using each system's
  // actual StartUs/EndUs from `SystemTickSummary`. Without them, bars fall back to a uniform sub-segment width
  // around the tick's center.
  systemTickSummaries?: readonly { tickNumber: number; systemIndex: number; startUs: number; durationUs: number }[],
  tickSummaries?: readonly TickSummaryDto[],
  // Phase axis — bars use this to project their (tick, sysStartUs, sysEndUs) onto normalized [0, 1] phase-space.
  // When null, the panel is in fallback mode (no phases or no system summaries) and bars are spread evenly across
  // [0, 1]. Required for phase-segmented X to actually apply (spec §6.1).
  phaseAxis?: PhaseAxis | null,
  // The single tick the panel is rendering — when set, only bars from this tick are emitted (single-tick replay,
  // spec D8). When null, all ticks in `touches` are emitted (legacy fallback used when phaseAxis is also absent).
  singleTick?: number | null,
): Bar[] {
  if (touches.length === 0 || tracks.length === 0 || !topology) return [];

  // Mirror trackBuilding's fallback chain so bars land on track ids that actually exist. Without this, a topology
  // with no componentFamilies (the typical case for engine-emitted traces) silently drops every bar at user-visible
  // L2 — `buildL2` quietly returns L1 phase rows, the unfixed L2 fan-out below emits `family:*` ids that don't match.
  level = resolveEffectiveLevel(topology, level);

  // Pre-compute lookups used by the inner loop. Sized once per build, reused across every touch row.
  const systems = topology.systems ?? [];
  const archetypes = topology.archetypes ?? [];
  const families = topology.componentFamilies?.componentToFamily ?? {};

  const systemIndexToName = new Map<number, string>();
  const systemIndexToPhase = new Map<number, string>();
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue(s.index);
    if (idx == null) continue;
    systemIndexToName.set(idx, s.name);
    systemIndexToPhase.set(idx, s.phaseName ?? '');
  }

  const archetypeById = new Map<number, { components: string[] }>();
  for (const a of archetypes) {
    const archId = typeof a.archetypeId === 'string' ? Number(a.archetypeId) : a.archetypeId;
    if (!Number.isFinite(archId)) continue;
    archetypeById.set(archId, { components: a.componentTypeNames ?? [] });
  }

  // Pre-index per-(tick, system) timing for sub-tick bar placement. Key format: `${tick}|${sysIdx}` → {startUs, durationUs}.
  // Tick-level start/duration is keyed by tickNumber.
  const sysTickTiming = new Map<string, { startUs: number; durationUs: number }>();
  for (const s of systemTickSummaries ?? []) {
    const t = numberValue((s as { tickNumber?: unknown }).tickNumber);
    const sx = numberValue((s as { systemIndex?: unknown }).systemIndex);
    const su = numberValue((s as { startUs?: unknown }).startUs);
    const du = numberValue((s as { durationUs?: unknown }).durationUs);
    if (t == null || sx == null || su == null || du == null) continue;
    sysTickTiming.set(`${t}|${sx}`, { startUs: su, durationUs: du });
  }
  const tickTiming = new Map<number, { startUs: number; durationUs: number }>();
  for (const ts of tickSummaries ?? []) {
    const t = numberValue((ts as { tickNumber?: unknown }).tickNumber);
    const su = numberValue((ts as { startUs?: unknown }).startUs);
    const du = numberValue((ts as { durationUs?: unknown }).durationUs);
    if (t == null || su == null || du == null) continue;
    tickTiming.set(t, { startUs: su, durationUs: du });
  }

  const out: Bar[] = [];

  for (const raw of touches) {
    const tick = numberValue((raw as { tickNumber?: unknown }).tickNumber);
    const sysIdx = numberValue((raw as { systemIndex?: unknown }).systemIndex);
    const archId = numberValue((raw as { archetypeId?: unknown }).archetypeId);
    const entities = numberValue((raw as { entityCount?: unknown }).entityCount) ?? 0;
    const chunks = numberValue((raw as { chunkCount?: unknown }).chunkCount) ?? 0;
    if (tick == null || sysIdx == null || archId == null) continue;
    // Single-tick replay (spec D8): emit bars only for the dominant tick. Other ticks are off-canvas in this mode;
    // the user picks a different tick via the Profiler, or switches to envelope/density modes for cross-tick aggregation.
    if (singleTick != null && tick !== singleTick) continue;

    const systemName = systemIndexToName.get(sysIdx);
    if (!systemName) continue;
    const phaseName = systemIndexToPhase.get(sysIdx) ?? '';
    const archetype = archetypeById.get(archId);

    // Position the bar in normalized [0, 1] phase-space. When phaseAxis is supplied (the common case) this maps
    // the system's (startUs, endUs) through its phase's segment + per-tick phase span. When phaseAxis is absent,
    // we still produce coordinates in [0, 1] — but using a synthetic placement: each tick's full µs span maps
    // linearly onto [0, 1], and the bar's sub-tick fraction lands at its real position.
    let xStart = 0;
    let xEnd = 1;
    const sysTiming = sysTickTiming.get(`${tick}|${sysIdx}`);
    const tTiming = tickTiming.get(tick);
    if (phaseAxis && sysTiming) {
      const sysEnd = sysTiming.startUs + sysTiming.durationUs;
      const mapped = tickOffsetToNormalizedX(phaseAxis, tick, phaseName, sysTiming.startUs, sysEnd);
      if (mapped) {
        xStart = mapped.xStart;
        xEnd = mapped.xEnd;
      } else if (tTiming && tTiming.durationUs > 0) {
        // Phase not in the axis (rare — system declares a phase that isn't in topology.phases). Fall back to the
        // bar's tick-relative fraction in [0, 1].
        xStart = Math.max(0, Math.min(1, sysTiming.startUs / tTiming.durationUs));
        xEnd = Math.max(0, Math.min(1, sysEnd / tTiming.durationUs));
      }
    } else if (sysTiming && tTiming && tTiming.durationUs > 0) {
      const sysEnd = sysTiming.startUs + sysTiming.durationUs;
      xStart = Math.max(0, Math.min(1, sysTiming.startUs / tTiming.durationUs));
      xEnd = Math.max(0, Math.min(1, sysEnd / tTiming.durationUs));
    }
    if (xEnd <= xStart) xEnd = Math.min(1, xStart + 0.001);

    // Common bar template — only `trackId` differs across the fan-out for a given event.
    const template = { tickNumber: tick, xStart, xEnd, phaseName, systemName, archetypeId: archId, entityCount: entities, chunkCount: chunks };

    switch (level) {
      case 'L0':
        // Single bar on the components-domain row. Queue / resource events ride other tracks (not yet emitted).
        out.push({ ...template, trackId: 'domain:components' });
        break;
      case 'L1': {
        const phaseName = systemIndexToPhase.get(sysIdx) ?? '';
        if (phaseName) {
          out.push({ ...template, trackId: `phase:${phaseName}/components` });
        } else {
          out.push({ ...template, trackId: 'domain:components' });
        }
        break;
      }
      case 'L2': {
        // Fan out per family hit by any of the archetype's components.
        const components = archetype?.components ?? [];
        const seenFamilies = new Set<string>();
        for (const c of components) {
          const family = families[c];
          if (!family) continue;
          if (seenFamilies.has(family)) continue;
          seenFamilies.add(family);
          out.push({ ...template, trackId: `family:${family}` });
        }
        // When the archetype has no components or none mapped to a family, drop on the queue/resource fallback rows
        // — but we don't have a per-event domain assignment yet, so we just skip silently. Future enhancement: emit
        // an "Unclassified" track at L2 to surface the gap.
        break;
      }
      case 'L3': {
        // One bar per component on the archetype.
        const components = archetype?.components ?? [];
        for (const c of components) {
          out.push({ ...template, trackId: `component:${c}` });
        }
        break;
      }
      case 'L4': {
        // One bar per (archetype, component) pair — most granular.
        const components = archetype?.components ?? [];
        for (const c of components) {
          out.push({ ...template, trackId: `archcomp:${archId}:${c}` });
        }
        break;
      }
    }
  }

  return out;
}

/**
 * Range-aggregate p5–p95 envelope (spec §7). Per (track, systemName), reduce the replay-mode bars produced for
 * the visible range into a single envelope bar whose [xStart, xEnd] covers the 5th–95th percentile of the bar
 * starts/ends. Answers "is this system stable or jittery across ticks?".
 *
 * Implementation: collect xStart and xEnd values per (track, system), sort each, then index-lookup the 5/95
 * percentiles. Single pass over sources, two sorts per output bar — at the typical 200 ticks × 80 systems × N tracks
 * scale that's still well under a millisecond. We tag the resulting bar's tickNumber as -1 so hover-isolate matches
 * by (systemName, -1) — i.e. "isolate this system across the envelope" without a specific tick.
 */
export function buildEnvelopeBars(replayBars: readonly Bar[]): Bar[] {
  if (replayBars.length === 0) return [];
  type Acc = { starts: number[]; ends: number[]; sample: Bar };
  const groups = new Map<string, Acc>();
  for (const b of replayBars) {
    const key = `${b.trackId}|${b.systemName}`;
    let acc = groups.get(key);
    if (!acc) {
      acc = { starts: [], ends: [], sample: b };
      groups.set(key, acc);
    }
    acc.starts.push(b.xStart);
    acc.ends.push(b.xEnd);
  }
  const out: Bar[] = [];
  for (const acc of groups.values()) {
    acc.starts.sort((a, b) => a - b);
    acc.ends.sort((a, b) => a - b);
    const p5Start = pct(acc.starts, 0.05);
    const p95End = pct(acc.ends, 0.95);
    if (p95End <= p5Start) continue;
    out.push({
      ...acc.sample,
      tickNumber: -1,
      xStart: p5Start,
      xEnd: p95End,
    });
  }
  return out;
}

function pct(sortedAsc: readonly number[], p: number): number {
  if (sortedAsc.length === 0) return 0;
  const idx = Math.min(sortedAsc.length - 1, Math.max(0, Math.round(p * (sortedAsc.length - 1))));
  return sortedAsc[idx];
}

/**
 * Per-(track, phase) heat strip cell — the data unit produced by density-aggregation mode. Each cell renders as
 * a rectangle filling its phase segment on its track row, alpha-modulated by `touchCount`. The renderer dispatches
 * to a dedicated draw path for these cells (instead of treating them as Bars), since they're a wholly different
 * visual primitive from the per-bar rectangles of replay/envelope.
 */
export interface DensityCell {
  readonly trackId: string;
  readonly phaseName: string;
  readonly touchCount: number;
}

/**
 * Per (track, phase), count the number of replay-bars in the range. Drives the density heatmap. Empty cells are
 * not emitted — the renderer leaves their phase × row area blank (the row's track-label still shows).
 */
export function buildDensityCells(replayBars: readonly Bar[]): DensityCell[] {
  if (replayBars.length === 0) return [];
  const counts = new Map<string, { trackId: string; phaseName: string; n: number }>();
  for (const b of replayBars) {
    if (!b.phaseName) continue;
    const key = `${b.trackId}|${b.phaseName}`;
    let cell = counts.get(key);
    if (!cell) {
      cell = { trackId: b.trackId, phaseName: b.phaseName, n: 0 };
      counts.set(key, cell);
    }
    cell.n++;
  }
  const out: DensityCell[] = [];
  for (const c of counts.values()) {
    out.push({ trackId: c.trackId, phaseName: c.phaseName, touchCount: c.n });
  }
  return out;
}

/**
 * Resolve the dominant access kind for a system on a given component. Used by the uPlot wrapper to color
 * each bar — write > readsFresh > readsSnapshot > reads > additionalReads. Returns 'none' when the system
 * doesn't list the component in any access set (a bar would still render thanks to the archetype membership,
 * but it'd be neutral-colored).
 *
 * Pure helper exported for the side panel + bar coloring.
 */
export type AccessKind = 'write' | 'side-write' | 'reads-fresh' | 'reads-snapshot' | 'reads' | 'additional-reads' | 'none';

export function accessKindFor(system: SystemDefinitionDto, componentName: string): AccessKind {
  if (containsName(system.writes, componentName)) return 'write';
  if (containsName(system.sideWrites, componentName)) return 'side-write';
  if (containsName(system.readsFresh, componentName)) return 'reads-fresh';
  if (containsName(system.readsSnapshot, componentName)) return 'reads-snapshot';
  if (containsName(system.reads, componentName)) return 'reads';
  if (containsName(system.additionalReads, componentName)) return 'additional-reads';
  return 'none';
}

/**
 * Aggregate access kind for a system across an entire data domain (used by L0/L1 domain rows where the bar isn't
 * tied to a single component — color the bar by the system's strongest declared access on ANY component, queue,
 * or resource). "Strongest" follows the same precedence order as {@link accessKindFor}.
 */
export function aggregateAccessKindForDomain(system: SystemDefinitionDto, domain: 'components' | 'queues' | 'resources'): AccessKind {
  if (domain === 'components') {
    if (hasAnyName(system.writes)) return 'write';
    if (hasAnyName(system.sideWrites)) return 'side-write';
    if (hasAnyName(system.readsFresh)) return 'reads-fresh';
    if (hasAnyName(system.readsSnapshot)) return 'reads-snapshot';
    if (hasAnyName(system.reads)) return 'reads';
    if (hasAnyName(system.additionalReads)) return 'additional-reads';
    return 'none';
  }
  if (domain === 'queues') {
    if (hasAnyName(system.writesEvents)) return 'write';
    if (hasAnyName(system.readsEvents)) return 'reads';
    return 'none';
  }
  // resources
  if (hasAnyName(system.writesResources)) return 'write';
  if (hasAnyName(system.readsResources)) return 'reads';
  return 'none';
}

function hasAnyName(arr: readonly string[] | null | undefined): boolean {
  return Array.isArray(arr) && arr.length > 0;
}

/**
 * Color palette for access kinds. Shared across panels (Data Flow Timeline bars, Access Matrix cells) so the
 * visual language stays consistent. Tailwind-700 family chosen for legibility on both light and dark themes.
 */
export const ACCESS_COLOR: Record<AccessKind, string> = {
  'write':            '#dc2626', // red-600
  'side-write':       '#ea580c', // orange-600
  'reads-fresh':      '#2563eb', // blue-600
  'reads-snapshot':   '#0891b2', // cyan-600
  'reads':            '#65a30d', // lime-600
  'additional-reads': '#84cc16', // lime-500
  'none':             '#94a3b8', // slate-400
};

function containsName(arr: readonly string[] | null | undefined, target: string): boolean {
  if (!arr) return false;
  for (let i = 0; i < arr.length; i++) {
    if (arr[i] === target) return true;
  }
  return false;
}

function numberValue(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string') {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}
