import { timeEqual, useSelectionStore, type SelectionState, type TimeSelection } from './useSelectionStore';

/**
 * URL ↔ selection-store sync for stable dimensions.
 *
 * Stable (URL-synced): `time`, `system`, `component`, `queue`, `resource`, `entity`.
 * Volatile (in-memory only): `focusTick`, `worker`, panel-internal sub-selections.
 *
 * See `claude/design/workbench/10-internal-data-api.md §9.3`.
 *
 * Wire format example: `?session=...&time=120000-134000&system=AI&queue=Damage`.
 */

const PARAM_TIME = 'time';
const PARAM_SYSTEM = 'system';
const PARAM_COMPONENT = 'component';
const PARAM_QUEUE = 'queue';
const PARAM_RESOURCE = 'resource';
const PARAM_ENTITY = 'entity';

const STABLE_PARAMS = [
  PARAM_TIME,
  PARAM_SYSTEM,
  PARAM_COMPONENT,
  PARAM_QUEUE,
  PARAM_RESOURCE,
  PARAM_ENTITY,
] as const;

export interface ParsedSelection {
  time: TimeSelection | null;
  system: string | null;
  component: string | null;
  queue: string | null;
  resource: string | null;
  entity: string | null;
}

function parseTimeRange(raw: string | null): TimeSelection | null {
  if (!raw) return null;
  const dash = raw.indexOf('-');
  if (dash <= 0) return null;
  const start = Number(raw.slice(0, dash));
  const end = Number(raw.slice(dash + 1));
  if (!Number.isFinite(start) || !Number.isFinite(end)) return null;
  if (end <= start) return null;
  return { start, end };
}

function formatTimeRange(t: TimeSelection): string {
  return `${t.start}-${t.end}`;
}

/**
 * Parses the stable selection slots from a URL query string. Returns null for missing or
 * malformed entries — never throws. Volatile slots (`focusTick`, `worker`) are not parsed.
 */
export function parseSelectionFromSearch(search: string): ParsedSelection {
  const params = new URLSearchParams(search);
  return {
    time: parseTimeRange(params.get(PARAM_TIME)),
    system: params.get(PARAM_SYSTEM),
    component: params.get(PARAM_COMPONENT),
    queue: params.get(PARAM_QUEUE),
    resource: params.get(PARAM_RESOURCE),
    entity: params.get(PARAM_ENTITY),
  };
}

/**
 * Builds an updated `URLSearchParams` from `current` by writing the stable slots of `state`.
 * Non-selection params (e.g. `?session=...`) are preserved. Null slots are removed.
 */
export function buildSelectionSearch(
  current: URLSearchParams,
  state: Pick<SelectionState, 'time' | 'system' | 'component' | 'queue' | 'resource' | 'entity'>,
): URLSearchParams {
  const out = new URLSearchParams(current);
  // Wipe any prior stable-selection params first so removals propagate.
  for (const name of STABLE_PARAMS) out.delete(name);
  if (state.time) out.set(PARAM_TIME, formatTimeRange(state.time));
  if (state.system) out.set(PARAM_SYSTEM, state.system);
  if (state.component) out.set(PARAM_COMPONENT, state.component);
  if (state.queue) out.set(PARAM_QUEUE, state.queue);
  if (state.resource) out.set(PARAM_RESOURCE, state.resource);
  if (state.entity) out.set(PARAM_ENTITY, state.entity);
  return out;
}

/**
 * Apply a parsed selection to the store. Setters are value-equal-aware (see
 * {@link useSelectionStore}), so calling each unconditionally is cheap.
 */
export function applySelectionToStore(parsed: ParsedSelection): void {
  const s = useSelectionStore.getState();
  s.setTime(parsed.time);
  s.setSystem(parsed.system);
  s.setComponent(parsed.component);
  s.setQueue(parsed.queue);
  s.setResource(parsed.resource);
  s.setEntity(parsed.entity);
}

function stableSnapshot(s: SelectionState) {
  return {
    time: s.time,
    system: s.system,
    component: s.component,
    queue: s.queue,
    resource: s.resource,
    entity: s.entity,
  };
}

function stableEqual(a: ReturnType<typeof stableSnapshot>, b: ReturnType<typeof stableSnapshot>) {
  return (
    timeEqual(a.time, b.time) &&
    a.system === b.system &&
    a.component === b.component &&
    a.queue === b.queue &&
    a.resource === b.resource &&
    a.entity === b.entity
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
 * Returns an unsubscribe handle. Idempotent — calling it twice yields two independent subs;
 * tests should always tear down via the returned function.
 *
 * Cold-load behaviour is the caller's responsibility: invoke {@link applySelectionToStore}
 * with the result of {@link parseSelectionFromSearch} once at app mount, then install this.
 */
export function installSelectionUrlSync(options: UrlSyncOptions = {}): () => void {
  const replace = options.replaceState ?? defaultReplaceState;
  const readSearch = options.readSearch ?? defaultReadSearch;
  let last = stableSnapshot(useSelectionStore.getState());

  return useSelectionStore.subscribe((s) => {
    const next = stableSnapshot(s);
    if (stableEqual(last, next)) return;
    last = next;
    const params = buildSelectionSearch(new URLSearchParams(readSearch()), next);
    const query = params.toString();
    replace(query.length > 0 ? `?${query}` : '');
  });
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
