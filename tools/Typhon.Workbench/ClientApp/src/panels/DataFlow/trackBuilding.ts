import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { GranularityLevel } from './useDataFlowViewStore';

/**
 * Pure track-list construction for the Data Flow Timeline. One row per track on the Y axis at the chosen
 * granularity. Output is stable-ordered so the timeline lays out the same way every render.
 *
 * Design reference: `claude/design/workbench/modules/14-data-flow.md` §7. The five levels descend from
 * the broadest "what's the engine doing across all data?" view (L0) to the finest per-(archetype, component)
 * cross-section (L4). Default is L2 (Component-family) — the right altitude for everyday flow questions.
 */
export type TrackKind =
  /** L0 / L1 row that aggregates every component across the engine. */
  | 'component-domain'
  /** L0 / L1 row that aggregates every event queue. */
  | 'queue-domain'
  /** L0 / L1 row that aggregates every named resource. */
  | 'resource-domain'
  /** L2 row keyed by component family name. */
  | 'component-family'
  /** L3 row keyed by component type name. */
  | 'component'
  /** L4 row keyed by (archetype id, component name). */
  | 'archetype-component'
  /** L0 / L1 row keyed by event queue name. (At higher levels each queue becomes its own row.) */
  | 'queue'
  /** Reserved for resource graph integration. Not populated yet — the resource side ships with the panel
   *  but rolls up to a single row at L0/L1 until per-resource events feed in. */
  | 'resource';

export interface Track {
  /** Stable identifier — used as React key + uPlot series id. Format depends on kind, see {@link TrackKind}. */
  readonly id: string;
  /** Human-readable label rendered as the row title. */
  readonly label: string;
  /** Discriminator for fan-out logic in `barBuilding`. */
  readonly kind: TrackKind;
  /**
   * Optional phase scope — when set, the track only renders bars whose system runs in this phase
   * (used by L1 to split each domain row across phases).
   */
  readonly phaseName?: string;
  /**
   * Optional component-name scope — set on `component`, `component-family`, `archetype-component` tracks.
   * `barBuilding` uses this to decide whether a (system, archetype) event hits this row.
   */
  readonly componentName?: string;
  /**
   * Optional family-name scope — set on `component-family` tracks. Membership test is done via the
   * topology's `componentFamilies.componentToFamily` map.
   */
  readonly familyName?: string;
  /**
   * Optional archetype id scope — set on `archetype-component` tracks. Restricts the row to bars
   * emitted for this specific archetype.
   */
  readonly archetypeId?: number;
}

/**
 * Build the ordered list of tracks for the requested granularity. Returns an empty array when topology
 * is null (panel renders the empty state until metadata lands).
 */
export function buildTracks(topology: TopologyDto | null, level: GranularityLevel): Track[] {
  if (!topology) return [];

  switch (level) {
    case 'L0':
      return buildL0(topology);
    case 'L1':
      return buildL1(topology);
    case 'L2':
      return buildL2(topology);
    case 'L3':
      return buildL3(topology);
    case 'L4':
      return buildL4(topology);
  }
}

/**
 * Resolve the *effective* granularity level for a topology — what {@link buildTracks} actually emitted after
 * fallbacks (L3→L2 when componentTypes is empty, L2→L1 when fewer than 8 families, L1→L0 when no phases).
 * Both `buildTracks` and `buildBars` must agree on the effective level, otherwise bars get emitted on track ids
 * that don't exist (silent drop). Pure, no allocations beyond the level lookup.
 */
export function resolveEffectiveLevel(topology: TopologyDto | null, requested: GranularityLevel): GranularityLevel {
  if (!topology) return requested;
  const phases = topology.phases ?? [];
  const families = topology.componentFamilies?.familyOrder ?? [];
  const componentTypes = topology.componentTypes ?? [];
  // Walk the same fallback chain buildTracks uses, in order from finest → coarsest.
  if (requested === 'L3' && componentTypes.length === 0) requested = 'L2';
  if (requested === 'L2' && families.length < 8) requested = 'L1';
  if (requested === 'L1' && phases.length === 0) requested = 'L0';
  // D9 auto-promote (mirrors buildL2): if L2 would be degenerate (each family ↔ 1 component), use L3.
  if (requested === 'L2' && everyFamilyHasOneComponent(topology)) requested = 'L3';
  return requested;
}

/**
 * D9 helper: returns true when every component-family in the topology contains exactly one component type.
 * In that case L2's family axis collapses to L3's component axis with worse labels — better to render L3.
 */
function everyFamilyHasOneComponent(topology: TopologyDto): boolean {
  const map = topology.componentFamilies?.componentToFamily;
  if (!map) return false;
  const countByFamily = new Map<string, number>();
  for (const family of Object.values(map)) {
    countByFamily.set(family, (countByFamily.get(family) ?? 0) + 1);
  }
  if (countByFamily.size === 0) return false;
  for (const count of countByFamily.values()) {
    if (count > 1) return false;
  }
  return true;
}

/**
 * L0 — three fixed rows: Components / Event Queues / Resources. The broadest "where is the tick spending its time"
 * view. Always present even if a domain is empty (zero bars on that row is itself a useful signal).
 */
function buildL0(_topology: TopologyDto): Track[] {
  return [
    { id: 'domain:components', label: 'Components', kind: 'component-domain' },
    { id: 'domain:queues',     label: 'Event Queues', kind: 'queue-domain' },
    { id: 'domain:resources',  label: 'Resources', kind: 'resource-domain' },
  ];
}

/**
 * L1 — three domain rows × N phase rows. Phase ordering follows {@link TopologyDto.phases} (RFC 07 declared order).
 * When the topology has no phases (legacy traces), L1 collapses back to L0.
 */
function buildL1(topology: TopologyDto): Track[] {
  const phases = topology.phases ?? [];
  if (phases.length === 0) return buildL0(topology);

  const tracks: Track[] = [];
  for (const phase of phases) {
    tracks.push({ id: `phase:${phase}/components`, label: `Components — ${phase}`, kind: 'component-domain', phaseName: phase });
    tracks.push({ id: `phase:${phase}/queues`,     label: `Event Queues — ${phase}`, kind: 'queue-domain', phaseName: phase });
    tracks.push({ id: `phase:${phase}/resources`,  label: `Resources — ${phase}`, kind: 'resource-domain', phaseName: phase });
  }
  return tracks;
}

/**
 * L2 — one row per component family + one per queue + one row for resources. Default granularity. Family ordering
 * comes from `topology.componentFamilies.familyOrder` (server-side canonical order, stable across sessions).
 * Auto-fallback to L1 when the family map has fewer than 8 entries — design D9.
 */
function buildL2(topology: TopologyDto): Track[] {
  const familyOrder = topology.componentFamilies?.familyOrder ?? [];
  if (familyOrder.length < 8) return buildL1(topology);
  // D9 auto-promote: when every family carries exactly one component type, L2 has the same row
  // count as L3 but with arbitrary "family" names. Drop to L3 — same shape, but honest labels.
  if (everyFamilyHasOneComponent(topology)) return buildL3(topology);

  const tracks: Track[] = [];
  for (const family of familyOrder) {
    tracks.push({ id: `family:${family}`, label: family, kind: 'component-family', familyName: family });
  }
  // Queue rows surfaced individually at L2+; one per queue (no rollup) so users see queue-level pressure.
  // Resource rows still aggregate at L2 (per-resource L4 deferred to follow-up — see design §16).
  tracks.push({ id: 'domain:queues', label: 'Event Queues', kind: 'queue-domain' });
  tracks.push({ id: 'domain:resources', label: 'Resources', kind: 'resource-domain' });
  return tracks;
}

/**
 * L3 — one row per component type. The "specifically what's happening to Position?" altitude. Component order
 * follows {@link TopologyDto.componentTypes} (declaration order in the engine). When componentTypes is empty
 * (older traces with thin tables), falls back to L2.
 */
function buildL3(topology: TopologyDto): Track[] {
  const components = topology.componentTypes ?? [];
  if (components.length === 0) return buildL2(topology);

  const tracks: Track[] = [];
  for (const c of components) {
    if (!c.name) continue;
    tracks.push({ id: `component:${c.name}`, label: c.name, kind: 'component', componentName: c.name });
  }
  tracks.push({ id: 'domain:queues', label: 'Event Queues', kind: 'queue-domain' });
  tracks.push({ id: 'domain:resources', label: 'Resources', kind: 'resource-domain' });
  return tracks;
}

/**
 * L4 — one row per (archetype, component) pair. The "Position-on-Ant vs Position-on-Food have different access
 * patterns; show me both" view. Iterates archetypes in declaration order and emits one track per slot in each
 * archetype's `componentTypeNames`. Empty when topology has no archetypes (which is the case for traces that
 * predate the Phase A static-data export).
 */
function buildL4(topology: TopologyDto): Track[] {
  const archetypes = topology.archetypes ?? [];
  const tracks: Track[] = [];
  for (const a of archetypes) {
    const components = a.componentTypeNames ?? [];
    const archId = typeof a.archetypeId === 'string' ? Number(a.archetypeId) : a.archetypeId;
    if (!Number.isFinite(archId)) continue;
    const label = a.label || a.name || `arch ${archId}`;
    for (const c of components) {
      tracks.push({
        id: `archcomp:${archId}:${c}`,
        label: `${c} on ${label}`,
        kind: 'archetype-component',
        archetypeId: archId,
        componentName: c,
      });
    }
  }
  // Queue + resource rows still surface at L4 so the row count never drops to zero just because a session has no
  // archetype-component data yet. Users can still see queue activity and visually relate it to per-pair component work.
  tracks.push({ id: 'domain:queues', label: 'Event Queues', kind: 'queue-domain' });
  tracks.push({ id: 'domain:resources', label: 'Resources', kind: 'resource-domain' });
  return tracks;
}

/**
 * Project a {@link Track} into the cross-panel {@link DataTrackSelection} shape used by `useSelectionStore`.
 * Pure conversion — copies kind + id and surfaces the relevant scope fields. Returns null when the track kind
 * isn't a meaningful selection target (e.g. unimplemented `queue` / `resource` rows from L4 fallback).
 */
export function trackToDataTrackSelection(
  track: Track,
): {
  kind: Track['kind'];
  id: string;
  componentName?: string;
  familyName?: string;
  archetypeId?: number;
} | null {
  switch (track.kind) {
    case 'component':
    case 'archetype-component':
      return {
        kind: track.kind,
        id: track.id,
        componentName: track.componentName,
        archetypeId: track.archetypeId,
      };
    case 'component-family':
      return { kind: track.kind, id: track.id, familyName: track.familyName };
    case 'component-domain':
    case 'queue-domain':
    case 'resource-domain':
      return { kind: track.kind, id: track.id };
    case 'queue':
    case 'resource':
      // Reserved for future per-instance rows; not selected today.
      return null;
  }
}

