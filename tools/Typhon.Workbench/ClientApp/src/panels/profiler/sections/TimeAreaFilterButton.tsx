import * as React from 'react';
import { Filter } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { FilterTree, type FilterNode } from '@/components/ui/filterTree';
import { GAUGE_GROUPS } from '@/libs/profiler/canvas/gauges/region';
import type { TrackState } from '@/libs/profiler/model/uiTypes';
import { useProfilerSessionStore, ThreadKind, type SlotThreadInfo } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

const ENGINE_OP_LEAVES: Array<{ id: string; label: string }> = [
  { id: 'phases',       label: 'Phases'       },
  { id: 'page-cache',   label: 'Page Cache'   },
  { id: 'disk-io',      label: 'Disk IO'      },
  { id: 'transactions', label: 'Transactions' },
  { id: 'wal',          label: 'WAL'          },
  { id: 'checkpoint',   label: 'Checkpoint'   },
];

interface Props {
  /** Active slot indices in render order. Used to populate the Threads category leaves. */
  activeSlots: number[];
  /** Active system indices in render order — one leaf each under "Systems". */
  activeSystems: number[];
  /** Slot → friendly thread name (chunk-cache derived; passed through from TimeArea). */
  threadNamesBySlot: Record<number, string> | null | undefined;
  /** systemIndex → friendly system name. */
  systemNamesByIdx: Record<number, string> | null | undefined;
  /**
   * Unified slot → {name, kind} from `useProfilerCache.threadInfos`. Same data as
   * `useProfilerSessionStore.liveThreadInfos` in live mode, but ALSO populated in replay mode from
   * the chunk cache's persistent `threadKinds` map. The popup reads kinds from this prop so the
   * Main / Workers / Other split works in both modes; falls back to the session store if unset.
   */
  threadInfos?: Map<number, { name: string; kind: number }>;
}

/**
 * Filter icon mounted in the ruler-row gutter. Clicking opens a Radix popover containing a search box,
 * the {@link FilterTree}, and All/None quick-action buttons. Selections take effect immediately by
 * dispatching into the per-section visibility maps in `useProfilerSessionStore` (slots/systems —
 * ephemeral) and `useProfilerViewStore` (gauges/engine-ops — persisted).
 */
export function TimeAreaFilterButton({ activeSlots, activeSystems, threadNamesBySlot, systemNamesByIdx, threadInfos }: Props): React.JSX.Element {
  const [search, setSearch] = React.useState('');

  const liveThreadInfos = useProfilerSessionStore((s) => s.liveThreadInfos);
  const slotVisibility = useProfilerSessionStore((s) => s.slotVisibility);
  const systemVisibility = useProfilerSessionStore((s) => s.systemVisibility);
  const gaugeVisibility = useProfilerViewStore((s) => s.gaugeVisibility);
  const engineOpVisibility = useProfilerViewStore((s) => s.engineOpVisibility);

  const setManySlotVisibility = useProfilerSessionStore((s) => s.setManySlotVisibility);
  const setManySystemVisibility = useProfilerSessionStore((s) => s.setManySystemVisibility);
  const clearSlotVisibility = useProfilerSessionStore((s) => s.clearSlotVisibility);
  const clearSystemVisibility = useProfilerSessionStore((s) => s.clearSystemVisibility);
  const setManyGaugeVisibility = useProfilerViewStore((s) => s.setManyGaugeVisibility);
  const setManyEngineOpVisibility = useProfilerViewStore((s) => s.setManyEngineOpVisibility);
  const clearGaugeVisibility = useProfilerViewStore((s) => s.clearGaugeVisibility);
  const clearEngineOpVisibility = useProfilerViewStore((s) => s.clearEngineOpVisibility);
  const setManyCollapseState = useProfilerSessionStore((s) => s.setManyCollapseState);
  const setManyGaugeCollapse = useProfilerViewStore((s) => s.setManyGaugeCollapse);
  const spanColorMode = useProfilerViewStore((s) => s.spanColorMode);
  const setSpanColorMode = useProfilerViewStore((s) => s.setSpanColorMode);

  // ── Build the tree ───────────────────────────────────────────────────────────────────────────
  // Stable IDs: gauge-{n}, op-{n}, slot-{n}, system-{n}. Group ids are 'gauges', 'threads',
  // 'threads-main', 'threads-workers', 'threads-other', 'systems', 'engine-ops'.
  const tree: FilterNode[] = React.useMemo(() => {
    const isGaugeChecked = (id: string): boolean => gaugeVisibility[id] !== false;
    const isOpChecked = (id: string): boolean => engineOpVisibility[id] !== false;
    const isSlotChecked = (idx: number): boolean => slotVisibility[idx] !== false;
    const isSystemChecked = (idx: number): boolean => systemVisibility[idx] !== false;

    const gaugeNode: FilterNode = {
      kind: 'group', id: 'gauges', label: 'Gauges',
      // Only gauges support the 3-state cycle (`summary` / `expanded` / `double`); the
      // `supportsDouble` flag tells FilterTree to render the third action button on these rows.
      children: GAUGE_GROUPS.map((g) => ({
        kind: 'leaf', id: `gauge-leaf-${g.id}`, label: g.label, checked: isGaugeChecked(g.id), supportsDouble: true,
      })),
    };

    // Threads — split active slots by ThreadKind. Prefer the unified `threadInfos` prop (fed by
    // `useProfilerCache` and populated in BOTH replay and live modes); fall back to the live store
    // if upstream didn't pass it. Replay traces older than cache v4 lack the kind byte and end up
    // bucketed under Other — same fallback as a live attach with no ThreadInfo received yet.
    const slotKindOf = (slot: number): ThreadKind => {
      const fromProp = threadInfos?.get(slot);
      if (fromProp) return fromProp.kind as ThreadKind;
      const info: SlotThreadInfo | undefined = liveThreadInfos.get(slot);
      return info?.kind ?? ThreadKind.Other;
    };
    const slotLabel = (slot: number): string => threadNamesBySlot?.[slot] ?? `Slot ${slot}`;
    const buildSlotLeaves = (kind: ThreadKind): FilterNode[] => activeSlots
      .filter((s) => slotKindOf(s) === kind)
      .map((s) => ({
        kind: 'leaf', id: `slot-leaf-${s}`, label: slotLabel(s), checked: isSlotChecked(s),
      } as FilterNode));
    const mainLeaves = buildSlotLeaves(ThreadKind.Main);
    const workerLeaves = buildSlotLeaves(ThreadKind.Worker);
    const otherLeaves = [
      ...activeSlots.filter((s) => {
        const k = slotKindOf(s);
        return k === ThreadKind.Pool || k === ThreadKind.Other;
      }).map((s) => ({
        kind: 'leaf', id: `slot-leaf-${s}`, label: slotLabel(s), checked: isSlotChecked(s),
      } as FilterNode)),
    ];
    const threadsChildren: FilterNode[] = [];
    if (mainLeaves.length > 0) threadsChildren.push({ kind: 'group', id: 'threads-main', label: 'Main thread', children: mainLeaves });
    if (workerLeaves.length > 0) threadsChildren.push({ kind: 'group', id: 'threads-workers', label: 'Workers', children: workerLeaves });
    if (otherLeaves.length > 0) threadsChildren.push({ kind: 'group', id: 'threads-other', label: 'Other', children: otherLeaves });
    const threadsNode: FilterNode = { kind: 'group', id: 'threads', label: 'Threads', children: threadsChildren };

    const systemsNode: FilterNode = {
      kind: 'group', id: 'systems', label: 'Systems',
      children: activeSystems.map((idx) => ({
        kind: 'leaf', id: `system-leaf-${idx}`, label: systemNamesByIdx?.[idx] ?? `System ${idx}`, checked: isSystemChecked(idx),
      } as FilterNode)),
    };

    const opsNode: FilterNode = {
      kind: 'group', id: 'engine-ops', label: 'Engine Operations',
      children: ENGINE_OP_LEAVES.map((op) => ({
        kind: 'leaf', id: `op-leaf-${op.id}`, label: op.label, checked: isOpChecked(op.id),
      })),
    };

    return [gaugeNode, threadsNode, systemsNode, opsNode];
  }, [activeSlots, activeSystems, threadNamesBySlot, systemNamesByIdx, threadInfos, liveThreadInfos, slotVisibility, systemVisibility, gaugeVisibility, engineOpVisibility]);

  // ── Toggle dispatcher ────────────────────────────────────────────────────────────────────────
  // Leaf IDs encode their kind via the prefix (gauge-leaf-*, op-leaf-*, slot-leaf-*, system-leaf-*).
  // Decode and route to the appropriate batch setter — one store update per click regardless of
  // how many leaves changed.
  const onToggleLeaves = React.useCallback((leafIds: string[], next: 'checked' | 'unchecked'): void => {
    const visible = next === 'checked';
    const gaugeUpd: Record<string, boolean> = {};
    const opUpd: Record<string, boolean> = {};
    const slotUpd: Record<number, boolean> = {};
    const systemUpd: Record<number, boolean> = {};
    for (const id of leafIds) {
      if (id.startsWith('gauge-leaf-')) gaugeUpd[id.slice('gauge-leaf-'.length)] = visible;
      else if (id.startsWith('op-leaf-')) opUpd[id.slice('op-leaf-'.length)] = visible;
      else if (id.startsWith('slot-leaf-')) slotUpd[Number(id.slice('slot-leaf-'.length))] = visible;
      else if (id.startsWith('system-leaf-')) systemUpd[Number(id.slice('system-leaf-'.length))] = visible;
    }
    if (Object.keys(gaugeUpd).length > 0) setManyGaugeVisibility(gaugeUpd);
    if (Object.keys(opUpd).length > 0) setManyEngineOpVisibility(opUpd);
    if (Object.keys(slotUpd).length > 0) setManySlotVisibility(slotUpd);
    if (Object.keys(systemUpd).length > 0) setManySystemVisibility(systemUpd);
  }, [setManyGaugeVisibility, setManyEngineOpVisibility, setManySlotVisibility, setManySystemVisibility]);

  // Collapse-state dispatcher. Routes by leaf-id prefix:
  //   - `gauge-leaf-X`   → setManyGaugeCollapse — gauges support all 3 states (`summary` / `expanded` / `double`).
  //   - everything else  → setManyCollapseState — non-gauge tracks are 2-state, so coerce `'double'` to `'expanded'`
  //     when the user clicks the third button on a non-gauge row (e.g. a worker slot under "Threads → Workers").
  // Track ids reconstruct from the leaf-id prefix:
  //   - slot-leaf-N      → track id = `slot-N`
  //   - system-leaf-N    → track id = `system-N`
  //   - op-leaf-X        → track id = `X` (already the literal track id like `page-cache`)
  //   - gauge-leaf-X     → track id = `X` (e.g. `gauge-memory`)
  const onSetCollapseState = React.useCallback((leafIds: string[], state: TrackState): void => {
    const gaugeUpd: Record<string, TrackState> = {};
    const otherUpd: Record<string, TrackState> = {};
    const nonGaugeState: TrackState = state === 'double' ? 'expanded' : state;
    for (const id of leafIds) {
      if (id.startsWith('gauge-leaf-')) {
        gaugeUpd[id.slice('gauge-leaf-'.length)] = state;
      } else if (id.startsWith('op-leaf-')) {
        otherUpd[id.slice('op-leaf-'.length)] = nonGaugeState;
      } else if (id.startsWith('slot-leaf-')) {
        otherUpd[`slot-${id.slice('slot-leaf-'.length)}`] = nonGaugeState;
      } else if (id.startsWith('system-leaf-')) {
        otherUpd[`system-${id.slice('system-leaf-'.length)}`] = nonGaugeState;
      }
    }
    if (Object.keys(gaugeUpd).length > 0) setManyGaugeCollapse(gaugeUpd);
    if (Object.keys(otherUpd).length > 0) setManyCollapseState(otherUpd);
  }, [setManyGaugeCollapse, setManyCollapseState]);

  const onClickAll = (): void => {
    clearGaugeVisibility();
    clearEngineOpVisibility();
    clearSlotVisibility();
    clearSystemVisibility();
  };
  const onClickNone = (): void => {
    setManyGaugeVisibility(Object.fromEntries(GAUGE_GROUPS.map((g) => [g.id, false])));
    setManyEngineOpVisibility(Object.fromEntries(ENGINE_OP_LEAVES.map((op) => [op.id, false])));
    setManySlotVisibility(Object.fromEntries(activeSlots.map((s) => [s, false])));
    setManySystemVisibility(Object.fromEntries(activeSystems.map((s) => [s, false])));
  };

  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label="Filter sections"
          title="Filter sections"
          className="flex items-center justify-center h-5 w-5 rounded-sm text-muted-foreground hover:text-foreground hover:bg-accent/60"
        >
          <Filter className="h-3.5 w-3.5" />
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" sideOffset={4} className="w-80 p-2">
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium">Sections</span>
        </div>
        <Input
          autoFocus
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search…"
          className="h-7 text-sm mb-2"
        />
        <div className="max-h-[60vh] overflow-y-auto pr-1">
          <FilterTree nodes={tree} searchText={search} onToggleLeaves={onToggleLeaves} onSetCollapseState={onSetCollapseState} />
        </div>
        <div className="flex items-center gap-2 pt-2 mt-2 border-t border-border">
          <Button size="sm" variant="outline" className="h-7 px-2" onClick={onClickAll}>All</Button>
          <Button size="sm" variant="outline" className="h-7 px-2" onClick={onClickNone}>None</Button>
        </div>
        {/*
         * Color spans by — alternative lenses on the same data:
         *   name     → hash span name into the palette (default; pre-toggle behaviour)
         *   thread   → palette[threadSlot]; spots cross-thread patterns
         *   depth    → palette[depth]; visualises call-stack nesting
         *   duration → log-scale heat ramp (blue → green → orange → red); makes outliers pop
         * Persisted in `useProfilerViewStore` so the chosen lens survives reload.
         */}
        <label className="flex items-center gap-2 pt-2 mt-2 border-t border-border text-xs">
          <span className="text-muted-foreground shrink-0">Color spans by</span>
          <select
            value={spanColorMode}
            onChange={(e) => setSpanColorMode(e.target.value as typeof spanColorMode)}
            className="flex-1 h-7 rounded-sm border border-input bg-background px-2 text-sm"
          >
            <option value="name">Name</option>
            <option value="thread">Thread</option>
            <option value="depth">Depth</option>
            <option value="duration">Duration (heat)</option>
          </select>
        </label>
      </PopoverContent>
    </Popover>
  );
}
