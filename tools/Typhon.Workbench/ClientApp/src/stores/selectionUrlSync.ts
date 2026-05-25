import { useProfilerViewStore } from './useProfilerViewStore';
import { useSelectionStore, type SelectionObjectType } from './useSelectionStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * URL ↔ selection sync for stable dimensions.
 *
 * Stable (URL-synced):
 *   - `time` (the µs viewport) — canonical home is `useProfilerViewStore.viewRange` since #345.
 *   - `system`, `component`, `queue`, `resource`, `entity` — `useSelectionStore`.
 *
 * Volatile (in-memory only): `worker`, panel-internal sub-selections, hover state.
 *
 * See `claude/design/Apps/Workbench/10-internal-data-api.md §9.3`.
 *
 * Wire format example: `?session=...&time=120000-134000&system=AI&queue=Damage`.
 */

const PARAM_TIME = 'time';
const PARAM_SYSTEM = 'system';
const PARAM_COMPONENT = 'component';
const PARAM_QUEUE = 'queue';
const PARAM_RESOURCE = 'resource';
const PARAM_ENTITY = 'entity';
const PARAM_LEAF = 'leaf';

const STABLE_PARAMS = [
  PARAM_TIME,
  PARAM_SYSTEM,
  PARAM_COMPONENT,
  PARAM_QUEUE,
  PARAM_RESOURCE,
  PARAM_ENTITY,
  PARAM_LEAF,
] as const;

/**
 * Inspector-leaf object types that round-trip through the URL as `?leaf=type:ref` (Stage 1, #373). Only
 * primitive-ref navigator objects qualify; rich-ref leaves (resource/field/entity/profiler/file-map)
 * are reconstructed from their panels, not the URL.
 */
const URL_LEAF_TYPES = new Set<SelectionObjectType>(['system', 'component', 'archetype']);

export interface ParsedLeaf {
  readonly type: SelectionObjectType;
  readonly ref: string;
}

function parseLeafParam(raw: string | null): ParsedLeaf | null {
  if (!raw) return null;
  const i = raw.indexOf(':');
  if (i <= 0 || i === raw.length - 1) return null;
  const type = raw.slice(0, i) as SelectionObjectType;
  if (!URL_LEAF_TYPES.has(type)) return null;
  return { type, ref: raw.slice(i + 1) };
}

function encodeLeaf(leaf: { type: SelectionObjectType; ref: unknown } | null): string | null {
  if (!leaf || !URL_LEAF_TYPES.has(leaf.type)) return null;
  if (typeof leaf.ref !== 'string' && typeof leaf.ref !== 'number') return null;
  return `${leaf.type}:${leaf.ref}`;
}

export interface ParsedSelection {
  /**
   * Parsed `?time=startUs-endUs`. The cold-load applier calls
   * {@link useProfilerViewStore.commitViewRange} with this — the profiler view store is the
   * canonical home of the viewport (no more `useSelectionStore.time` slot since #345).
   */
  viewRange: TimeRange | null;
  system: string | null;
  component: string | null;
  queue: string | null;
  resource: string | null;
  entity: string | null;
  /** Optional in callers; {@link parseSelectionFromSearch} always populates it (null when absent). */
  leaf?: ParsedLeaf | null;
}

function parseViewRange(raw: string | null): TimeRange | null {
  if (!raw) return null;
  const dash = raw.indexOf('-');
  if (dash <= 0) return null;
  const startUs = Number(raw.slice(0, dash));
  const endUs = Number(raw.slice(dash + 1));
  if (!Number.isFinite(startUs) || !Number.isFinite(endUs)) return null;
  if (endUs <= startUs) return null;
  return { startUs, endUs };
}

function formatViewRange(r: TimeRange): string {
  return `${r.startUs}-${r.endUs}`;
}

/**
 * Parses the stable selection slots from a URL query string. Returns null for missing or
 * malformed entries — never throws. Volatile slots (`worker`) are not parsed.
 */
export function parseSelectionFromSearch(search: string): ParsedSelection {
  const params = new URLSearchParams(search);
  return {
    viewRange: parseViewRange(params.get(PARAM_TIME)),
    system: params.get(PARAM_SYSTEM),
    component: params.get(PARAM_COMPONENT),
    queue: params.get(PARAM_QUEUE),
    resource: params.get(PARAM_RESOURCE),
    entity: params.get(PARAM_ENTITY),
    leaf: parseLeafParam(params.get(PARAM_LEAF)),
  };
}

interface UrlOutputState {
  viewRange: TimeRange | null;
  system: string | null;
  component: string | null;
  queue: string | null;
  resource: string | null;
  entity: string | null;
  /** Optional in callers; {@link snapshot} always provides it (encoded leaf or null). */
  leaf?: string | null;
}

/**
 * Builds an updated `URLSearchParams` from `current` by writing the stable slots of `state`.
 * Non-selection params (e.g. `?session=...`) are preserved. Null slots are removed.
 */
export function buildSelectionSearch(
  current: URLSearchParams,
  state: UrlOutputState,
): URLSearchParams {
  const out = new URLSearchParams(current);
  // Wipe any prior stable-selection params first so removals propagate.
  for (const name of STABLE_PARAMS) out.delete(name);
  if (state.viewRange) out.set(PARAM_TIME, formatViewRange(state.viewRange));
  if (state.system) out.set(PARAM_SYSTEM, state.system);
  if (state.component) out.set(PARAM_COMPONENT, state.component);
  if (state.queue) out.set(PARAM_QUEUE, state.queue);
  if (state.resource) out.set(PARAM_RESOURCE, state.resource);
  if (state.entity) out.set(PARAM_ENTITY, state.entity);
  if (state.leaf) out.set(PARAM_LEAF, state.leaf);
  return out;
}

/**
 * Apply a parsed selection to the canonical stores. Setters are value-equal-aware (see
 * {@link useSelectionStore}), so calling each unconditionally is cheap. The viewport goes to the
 * profiler view store via {@link useProfilerViewStore.commitViewRange} (atomic write — bypasses
 * the transient/debounce path since this is a one-shot cold-load action).
 */
export function applySelectionToStore(parsed: ParsedSelection): void {
  const s = useSelectionStore.getState();
  s.setSystem(parsed.system);
  s.setComponent(parsed.component);
  s.setQueue(parsed.queue);
  s.setResource(parsed.resource);
  s.setEntity(parsed.entity);
  if (parsed.viewRange) {
    useProfilerViewStore.getState().commitViewRange(parsed.viewRange);
  }
  // Apply the explicit Inspector-leaf last so it wins recency over the scalar projections above.
  if (parsed.leaf) {
    s.select(parsed.leaf.type, parsed.leaf.ref);
  }
}

function rangeEqual(a: TimeRange | null, b: TimeRange | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.startUs === b.startUs && a.endUs === b.endUs;
}

function snapshot(): UrlOutputState {
  const sel = useSelectionStore.getState();
  const vr = useProfilerViewStore.getState().viewRange;
  // Treat the `{0,0}` no-selection sentinel as "no viewport in URL" — otherwise every fresh open
  // would write `?time=0-0` which round-trips to a discarded zero-width range anyway.
  const exportedRange: TimeRange | null = vr.endUs > vr.startUs ? vr : null;
  return {
    viewRange: exportedRange,
    system: sel.system,
    component: sel.component,
    queue: sel.queue,
    resource: sel.resource,
    entity: sel.entity,
    leaf: encodeLeaf(sel.leaf),
  };
}

function snapshotEqual(a: UrlOutputState, b: UrlOutputState): boolean {
  return (
    rangeEqual(a.viewRange, b.viewRange) &&
    a.system === b.system &&
    a.component === b.component &&
    a.queue === b.queue &&
    a.resource === b.resource &&
    a.entity === b.entity &&
    a.leaf === b.leaf
  );
}

export interface UrlSyncOptions {
  /**
   * History API surface — pulled out so tests can inject a fake. Defaults to
   * `window.history.replaceState` bound to the current location.
   */
  replaceState?: (search: string) => void;
  /** Defaults to `window.location.search`. */
  readSearch?: () => string;
}

/**
 * Installs a subscription that mirrors stable selection slots to the URL via `replaceState`.
 * Subscribes to both `useSelectionStore` (for system/component/queue/resource/entity) and
 * `useProfilerViewStore.viewRange` (for the `?time=` parameter). Returns a combined unsubscribe
 * handle. Idempotent — calling it twice yields two independent subs; tests should always tear
 * down via the returned function.
 *
 * Cold-load behaviour is the caller's responsibility: invoke {@link applySelectionToStore}
 * with the result of {@link parseSelectionFromSearch} once at app mount, then install this.
 */
export function installSelectionUrlSync(options: UrlSyncOptions = {}): () => void {
  const replace = options.replaceState ?? defaultReplaceState;
  const readSearch = options.readSearch ?? defaultReadSearch;
  let last = snapshot();

  const emit = (): void => {
    const next = snapshot();
    if (snapshotEqual(last, next)) return;
    last = next;
    const params = buildSelectionSearch(new URLSearchParams(readSearch()), next);
    const query = params.toString();
    replace(query.length > 0 ? `?${query}` : '');
  };

  // Each store fires emit independently; the snapshot+diff inside dedupes the two streams.
  const offSelection = useSelectionStore.subscribe(emit);
  // The view store updates `transientViewRange` on every pan/zoom frame, but only the committed `viewRange` reaches
  // the URL (see snapshot). Gate on a `viewRange` reference change so emit()'s snapshot+diff doesn't run every frame
  // during a drag — it now runs only when the viewport is actually committed.
  let lastViewRange = useProfilerViewStore.getState().viewRange;
  const offView = useProfilerViewStore.subscribe((state) => {
    if (state.viewRange === lastViewRange) {
      return;
    }
    lastViewRange = state.viewRange;
    emit();
  });
  return () => {
    offSelection();
    offView();
  };
}

function defaultReplaceState(search: string): void {
  if (typeof window === 'undefined') return;
  const url = `${window.location.pathname}${search}${window.location.hash}`;
  window.history.replaceState(window.history.state, '', url);
}

function defaultReadSearch(): string {
  if (typeof window === 'undefined') return '';
  return window.location.search;
}

