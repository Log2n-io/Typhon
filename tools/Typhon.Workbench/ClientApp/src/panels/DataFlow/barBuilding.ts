import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { Track } from './trackBuilding';
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
): Bar[] {
  if (touches.length === 0 || tracks.length === 0 || !topology) return [];

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

  const out: Bar[] = [];

  for (const raw of touches) {
    const tick = numberValue((raw as { tickNumber?: unknown }).tickNumber);
    const sysIdx = numberValue((raw as { systemIndex?: unknown }).systemIndex);
    const archId = numberValue((raw as { archetypeId?: unknown }).archetypeId);
    const entities = numberValue((raw as { entityCount?: unknown }).entityCount) ?? 0;
    const chunks = numberValue((raw as { chunkCount?: unknown }).chunkCount) ?? 0;
    if (tick == null || sysIdx == null || archId == null) continue;

    const systemName = systemIndexToName.get(sysIdx);
    if (!systemName) continue;
    const archetype = archetypeById.get(archId);

    // Common bar template — only `trackId` differs across the fan-out for a given event.
    const template = { tickNumber: tick, systemName, archetypeId: archId, entityCount: entities, chunkCount: chunks };

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
