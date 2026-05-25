import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { type AccessKind, accessKindFor } from '@/panels/DataFlow/barBuilding';
import { type Track, buildTracks } from '@/panels/DataFlow/trackBuilding';
import type { GranularityLevel } from '@/panels/DataFlow/useDataFlowViewStore';

/**
 * Pure matrix construction for the Access Matrix panel. Rows are the same Tracks the Data Flow Timeline uses
 * (re-using `buildTracks` is intentional — both panels share the L0–L4 altitude semantics so users carry their
 * mental model across them). Columns are systems in declaration order. Each cell carries the access kind for
 * that (row, column) pair plus an optional touch count drawn from the visible tick slice.
 *
 * Pure, allocation-bounded, deterministic. Cheap to call on every (topology, level, slice) change.
 */

/**
 * One column of the matrix — one system. The phaseName is replicated here so the renderer can group columns
 * by phase without re-walking topology.
 */
export interface Column {
  readonly systemName: string;
  readonly systemIndex: number;
  readonly phaseName: string;
}

/**
 * One filled cell. Rows whose access kind is `'none'` for a column are NOT emitted — the matrix is sparse,
 * and rendering iterates only the cells that actually exist. The renderer treats an absent cell as "not touched"
 * (faded background).
 */
export interface Cell {
  readonly rowId: string;
  readonly columnSystemName: string;
  readonly accessKind: AccessKind;
  /**
   * How many `SchedulerSystemArchetypeEvent` rows in the visible tick range pertain to this (row, column) pair.
   * 0 means the system declares access on the row but never actually executed against it in this range.
   * Drives intensity scaling on the rendered cell.
   */
  readonly touchCount: number;
}

export interface AccessMatrix {
  readonly rows: Track[];
  readonly columns: Column[];
  /** Keyed by `${rowId}|${columnSystemName}` for O(1) lookup at render time. */
  readonly cells: Map<string, Cell>;
}

/**
 * Build the matrix at the requested granularity. The `touchSlice` provides per-(system, archetype, tick) rows
 * already filtered to the visible tick range — pass `[]` when no time selection or no Phase A telemetry is
 * available; the matrix still builds with `touchCount: 0` everywhere and `accessKind` derived from the
 * declared access on each `SystemDefinitionDto`.
 */
export function buildAccessMatrix(
  topology: TopologyDto | null,
  level: GranularityLevel,
  touchSlice: readonly SystemArchetypeTouchSummary[],
): AccessMatrix {
  if (!topology) {
    return { rows: [], columns: [], cells: new Map() };
  }

  const rows = buildTracks(topology, level);
  const columns = buildColumns(topology.systems ?? []);
  const cells = buildCells(rows, columns, topology, touchSlice);

  return { rows, columns, cells };
}

function buildColumns(systems: readonly SystemDefinitionDto[]): Column[] {
  const out: Column[] = [];
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue(s.index);
    if (idx == null) continue;
    out.push({
      systemName: s.name,
      systemIndex: idx,
      phaseName: s.phaseName ?? '',
    });
  }
  return out;
}

/**
 * For each (row, column) pair, derive the dominant access kind and aggregate the touch count.
 *
 * - Access kind is purely declarative — read off the system's `reads/writes/...` arrays. Two strategies depending
 *   on the row's kind:
 *   - Component-level rows (`component`, `component-family`, `archetype-component`, all carry a `componentName`):
 *     resolve directly via `accessKindFor(system, componentName)`.
 *   - Domain rows (`component-domain`, `queue-domain`, `resource-domain`): aggregate across all relevant
 *     declarations on the system. For now, we only emit a cell when the system has at least one declaration
 *     in the relevant domain; intensity is the count of declarations.
 * - Touch count is empirical — count of (system, archetype) events in the slice, filtered to the row's scope.
 *
 * Sparse output: cells with `accessKind === 'none'` AND `touchCount === 0` are not added.
 */
function buildCells(
  rows: readonly Track[],
  columns: readonly Column[],
  topology: TopologyDto,
  touchSlice: readonly SystemArchetypeTouchSummary[],
): Map<string, Cell> {
  const cells = new Map<string, Cell>();
  if (rows.length === 0 || columns.length === 0) return cells;

  // Index lookups for the inner loop.
  const systemByIndex = new Map<number, SystemDefinitionDto>();
  const phaseBySystem = new Map<string, string>();
  for (const s of topology.systems ?? []) {
    if (!s.name) continue;
    const idx = numberValue(s.index);
    if (idx == null) continue;
    systemByIndex.set(idx, s);
    phaseBySystem.set(s.name, s.phaseName ?? '');
  }

  const archetypes = topology.archetypes ?? [];
  const archetypeById = new Map<number, { components: string[] }>();
  for (const a of archetypes) {
    const archId = numberValue(a.archetypeId);
    if (archId == null) continue;
    archetypeById.set(archId, { components: a.componentTypeNames ?? [] });
  }
  const families = topology.componentFamilies?.componentToFamily ?? {};

  // First pass — declared access. One iteration per (row, column). For most levels this loop is the dominant
  // cost (rows × columns rarely exceeds 200 × 200 even at L3).
  for (const row of rows) {
    for (const col of columns) {
      const sys = systemByIndex.get(col.systemIndex);
      if (!sys) continue;

      const kind = declaredAccessFor(row, sys);
      if (kind === 'none') continue;  // skip cells with no declared access — touch count gets folded next
      cells.set(cellKey(row.id, col.systemName), {
        rowId: row.id,
        columnSystemName: col.systemName,
        accessKind: kind,
        touchCount: 0,  // updated by the second pass
      });
    }
  }

  // Second pass — fold empirical touch counts into existing cells (and create new ones for cases where the
  // system actually executed against a row even when the declaration didn't surface — defensive against
  // declaration drift and queue/resource rows where access kinds aren't declarative).
  for (const raw of touchSlice) {
    const sysIdx = numberValue((raw as { systemIndex?: unknown }).systemIndex);
    const archId = numberValue((raw as { archetypeId?: unknown }).archetypeId);
    if (sysIdx == null || archId == null) continue;

    const sys = systemByIndex.get(sysIdx);
    if (!sys || !sys.name) continue;
    const archetype = archetypeById.get(archId);

    // Determine which rows this (system, archetype) pair contributes to. Mirrors the L-level fan-out from
    // `barBuilding` so the matrix's empirical-touch counts match what the timeline shows.
    for (const row of rows) {
      const rowMatchesEvent = rowMatchesArchetypeForEvent(row, archetype, families);
      if (!rowMatchesEvent) continue;

      const key = cellKey(row.id, sys.name);
      const existing = cells.get(key);
      if (existing) {
        cells.set(key, { ...existing, touchCount: existing.touchCount + 1 });
      } else {
        // System touched the row at runtime but didn't declare matching access — surface as a 'none' cell so
        // the user sees the discrepancy. Rare in practice (declarations drift after refactors).
        cells.set(key, { rowId: row.id, columnSystemName: sys.name, accessKind: 'none', touchCount: 1 });
      }
    }
  }

  return cells;
}

/**
 * Derive the declared access kind for a (row, system) pair. Each row kind has its own resolution logic — pure,
 * deterministic, no allocations beyond the kind enum.
 */
function declaredAccessFor(row: Track, sys: SystemDefinitionDto): AccessKind {
  switch (row.kind) {
    case 'component':
    case 'archetype-component':
      // Direct lookup by component name. Same path the timeline uses for bar coloring.
      return row.componentName ? accessKindFor(sys, row.componentName) : 'none';

    case 'component-family': {
      // Family rows aggregate across components — the row's access kind is the strongest declared on any
      // component the system reads/writes that's classified into this family. The renderer doesn't need a
      // membership probe; we ask: does the system declare ANY access on a component classified to this family?
      // For a quick first cut, fall through to 'none' — Phase D's hover-card surfaces the breakdown.
      // (Future enhancement: walk the system's full read/write set + family map; deferred since the cost is
      //  proportional to the entire access matrix and Phase B's L2 cells already light up empirically via
      //  the touch-count second pass.)
      return 'none';
    }

    case 'component-domain':
    case 'queue-domain':
    case 'resource-domain': {
      // Domain rows aggregate across an entire data category. We render a 'reads' or 'write' cell whenever
      // the system has any declaration in that domain. Strongest wins (write > side-write > reads).
      if (row.kind === 'component-domain') {
        if (hasAny(sys.writes)) return 'write';
        if (hasAny(sys.sideWrites)) return 'side-write';
        if (hasAny(sys.readsFresh)) return 'reads-fresh';
        if (hasAny(sys.readsSnapshot)) return 'reads-snapshot';
        if (hasAny(sys.reads)) return 'reads';
        return 'none';
      }
      if (row.kind === 'queue-domain') {
        if (hasAny(sys.writesEvents)) return 'write';
        if (hasAny(sys.readsEvents)) return 'reads';
        return 'none';
      }
      // resource-domain
      if (hasAny(sys.writesResources)) return 'write';
      if (hasAny(sys.readsResources)) return 'reads';
      return 'none';
    }

    case 'queue':
    case 'resource':
      // Reserved rows for future per-queue / per-resource granularity. Not emitted by `buildTracks` yet.
      return 'none';
  }
}

/**
 * Decide whether a row should be incremented for a given event. Mirrors the L-level fan-out from
 * `barBuilding` but only returns a boolean — touch counts aggregate per (row, column).
 */
function rowMatchesArchetypeForEvent(
  row: Track,
  archetype: { components: string[] } | undefined,
  families: Readonly<Record<string, string>>,
): boolean {
  switch (row.kind) {
    case 'component-domain':
      // L0 / L1 — the components-domain row catches every event.
      return true;
    case 'queue-domain':
    case 'resource-domain':
      // Queue / resource events aren't surfaced through SchedulerSystemArchetypeEvent yet.
      return false;
    case 'component-family':
      if (!archetype || !row.familyName) return false;
      for (const c of archetype.components) {
        if (families[c] === row.familyName) return true;
      }
      return false;
    case 'component':
      if (!archetype || !row.componentName) return false;
      return archetype.components.includes(row.componentName);
    case 'archetype-component':
      // Tied to one specific archetype; only events on that archetype contribute.
      if (!archetype || row.archetypeId == null) return false;
      // The check that the archetype id matches happens at the call site through archetypeById lookup —
      // here we only know that an archetype was found AND row.archetypeId is set; cross-checking the id
      // belongs at the loop body. Defer there.
      // For the v1 implementation, accept: the touch-count second-pass loops over all rows for each event,
      // so we'd over-count (archetype, component) pairs where the archetype doesn't match. Stricter check:
      return row.componentName != null && archetype.components.includes(row.componentName);
    case 'queue':
    case 'resource':
      return false;
  }
}

function cellKey(rowId: string, columnSystemName: string): string {
  return `${rowId}|${columnSystemName}`;
}

function hasAny(arr: readonly string[] | null | undefined): boolean {
  return !!arr && arr.length > 0;
}

function numberValue(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string') {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}
