import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { Camera } from 'lucide-react';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { useViewOptionsStore } from '@/stores/useViewOptionsStore';
import ParallelismPill from './ParallelismPill';
import { lastNTicksToTime, timeToTickRange } from './tickRangeMapping';
import { computeRangeUtilization } from './tickUtilization';
import { useDagViewStore, type LayoutMode, type StatMode } from './useDagViewStore';
import { useNodePositionsStore } from './useNodePositionsStore';

interface Props {
  /** Profiler-metadata tick rows — used for both the µs↔tick conversion and the auto-snapshot decision. */
  tickSummaries: readonly TickSummaryDto[] | null;
  /** Auto-snapshot once on first arrival of metadata when no time selection exists yet. */
  autoSnapshotEnabled: boolean;
  /** Per-(tick, system) duration rows — feeds the parallelism-inefficiency pill (A1 + A6). */
  systemTickSummaries: readonly SystemTickSummary[] | null;
  /** Worker pool size from the profiler header. Pill stays hidden when null / < 2 (parallelism is undefined). */
  workerCount: number | null;
}

const STAT_OPTIONS: Array<{ key: StatMode; label: string }> = [
  { key: 'mean', label: 'mean' },
  { key: 'p50', label: 'p50' },
  { key: 'p95', label: 'p95' },
  { key: 'p99', label: 'p99' },
  { key: 'max', label: 'max' },
];

/**
 * Layout choices exposed in the toolbar combo. Phase-aware layouts (horizontal/vertical lanes)
 * preserve the design's swim-lane skeleton; phase-agnostic layouts (compact/circular) drop the
 * lanes for cases where the user wants a different angle on the same topology.
 */
const LAYOUT_OPTIONS: Array<{ key: LayoutMode; label: string; description: string }> = [
  { key: 'horizontal-lanes', label: 'Horizontal lanes', description: 'Phases stack top-to-bottom; systems flow left-to-right within each phase' },
  { key: 'vertical-lanes', label: 'Vertical lanes', description: 'Phases as side-by-side columns; systems flow top-to-bottom within each phase' },
  { key: 'compact', label: 'Compact', description: 'Flat layered layout. No swim lanes; cross-phase edges are visible' },
  { key: 'circular', label: 'Circular', description: 'Systems on a circle, ordered by phase then name. All edges visible' },
];

const SNAPSHOT_TICK_COUNT = 600;

/**
 * Top-of-panel toolbar for the System DAG. Two controls per `09-system-dag.md §6.1` + §7.2:
 * the **stat-mode** selector that swaps the per-node primary stat aggregation, and the
 * **Snapshot last N ticks** action that pins both panels (DAG aggregation range + profiler
 * TimeArea) to a frozen window via `useSelectionStore.time`.
 *
 * After cross-panel binding (§7.1), the range readout reflects whatever µs window the user has
 * selected — whether they snapshotted here or scrubbed in the profiler. The selection store and
 * its bridges keep the two views in lockstep automatically.
 *
 * Auto-snapshot fires once on first metadata arrival when the time slot is null AND nothing has
 * been deep-linked from a URL — so a fresh open shows useful colour without a click.
 */
export default function SystemDagToolbar({ tickSummaries, autoSnapshotEnabled, systemTickSummaries, workerCount }: Props) {
  // Post-#345: time-window canonical source is the profiler view store. SystemDag aggregations
  // read the *committed* slot — the debounce upstream already ensures we only re-fetch after the
  // user stops scrubbing.
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);
  // "Has a real selection" — anything other than the `{0, 0}` no-selection sentinel.
  const hasTimeSelection = viewRange.endUs > viewRange.startUs;
  const statMode = useDagViewStore((s) => s.statMode);
  const setStatMode = useDagViewStore((s) => s.setStatMode);
  const layout = useDagViewStore((s) => s.layout);
  const setLayout = useDagViewStore((s) => s.setLayout);
  const hideSkipped = useDagViewStore((s) => s.hideSkipped);
  const setHideSkipped = useDagViewStore((s) => s.setHideSkipped);
  const showCrossPhaseEdges = useDagViewStore((s) => s.showCrossPhaseEdges);
  const setShowCrossPhaseEdges = useDagViewStore((s) => s.setShowCrossPhaseEdges);
  // Shared cross-panel setting, also editable from Options → DAG.
  const showEngineTracks = useViewOptionsStore((s) => s.showEngineSystems);
  const setShowEngineTracks = useViewOptionsStore((s) => s.setShowEngineSystems);
  const isLaneLayout = layout === 'horizontal-lanes' || layout === 'vertical-lanes';

  // Manual-position override count for the current layout — drives the "Reset positions"
  // button visibility. We subscribe to the overrides map (cheap object) rather than calling
  // `countForLayout()` once, so the button reactively appears/disappears as the user drags
  // tiles. Memoised to dodge a re-render storm during drag.
  const overrides = useNodePositionsStore((s) => s.overrides);
  const clearLayoutPositions = useNodePositionsStore((s) => s.clearLayout);
  const overrideCount = useMemo(() => {
    const prefix = `${layout}|`;
    let n = 0;
    for (const k of Object.keys(overrides)) if (k.startsWith(prefix)) n++;
    return n;
  }, [overrides, layout]);
  // Help glyph follows the app-wide `legendsVisible` flag (toggled via the `l` key or the
  // "Toggle Legends" palette command). Hidden when legends are off so chrome stays minimal.
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);
  const [helpOpen, setHelpOpen] = useState(false);

  const hasTicks = tickSummaries != null && tickSummaries.length > 0;

  // Translate the current time-window (µs) back to ticks for the readout. Cross-panel scrubs
  // round through the converter so the user sees consistent tick numbers regardless of who set
  // the window.
  const tickRange = useMemo(() => timeToTickRange(viewRange, tickSummaries), [viewRange, tickSummaries]);

  // Parallelism inefficiency over the selected range — drives the A1 pill + A6 sparkline. Pill
  // stays hidden (`null`) when worker count is missing / < 2 or the range has no usable ticks.
  // Recomputes only on the inputs that actually move; tickSummaries / systemTickSummaries are
  // referentially stable while the cache is loaded.
  const utilization = useMemo(
    () => computeRangeUtilization({
      workerCount: workerCount != null && workerCount >= 2 ? workerCount : null,
      tickSummaries,
      systemTickSummaries,
      range: tickRange,
    }),
    [workerCount, tickSummaries, systemTickSummaries, tickRange],
  );

  // Auto-snapshot once on first arrival when no selection exists yet (viewRange is the `{0,0}`
  // sentinel set by the metadata-arrival reset in ProfilerPanel). After that, snapshot is
  // user-driven (per §7.3 — no continuous live updates).
  useEffect(() => {
    if (!autoSnapshotEnabled) return;
    if (!hasTicks || hasTimeSelection || tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) commitViewRange(next);
  }, [autoSnapshotEnabled, hasTicks, hasTimeSelection, tickSummaries, commitViewRange]);

  const onSnapshotClick = () => {
    if (tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) commitViewRange(next);
  };

  // Reset to the `{0, 0}` no-selection sentinel — TimeArea treats this as "show the full trace"
  // and downstream aggregations (timeToTickRange returns null) skip their fetches.
  const onClearClick = () => commitViewRange({ startUs: 0, endUs: 0 });

  return (
    <div className="flex items-center gap-3 border-b border-border bg-background/95 px-3 py-1.5">
      <button
        type="button"
        disabled={!hasTicks}
        onClick={onSnapshotClick}
        className="flex items-center gap-1.5 rounded border border-border bg-card px-2 py-1 font-mono text-[11px] text-foreground hover:bg-muted disabled:cursor-not-allowed disabled:opacity-40"
        title={hasTicks ? `Pin both views to the last ${SNAPSHOT_TICK_COUNT} ticks` : 'Waiting for ticks…'}
      >
        <Camera className="h-3 w-3" />
        Snapshot last {SNAPSHOT_TICK_COUNT} ticks
      </button>

      <div className="flex items-center gap-1">
        <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">stat</span>
        <div className="flex overflow-hidden rounded border border-border">
          {STAT_OPTIONS.map((opt) => {
            const active = statMode === opt.key;
            return (
              <button
                key={opt.key}
                type="button"
                onClick={() => setStatMode(opt.key)}
                className={`px-2 py-0.5 font-mono text-[11px] ${active
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-card text-foreground hover:bg-muted'
                  }`}
                title={`Show ${opt.label} duration per system`}
              >
                {opt.label}
              </button>
            );
          })}
        </div>
      </div>

      <div className="flex items-center gap-1">
        <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">layout</span>
        <select
          value={layout}
          onChange={(e) => setLayout(e.target.value as LayoutMode)}
          className="rounded border border-border bg-card px-2 py-0.5 font-mono text-[11px] text-foreground hover:bg-muted focus:outline-none focus:ring-1 focus:ring-primary"
          title={LAYOUT_OPTIONS.find((o) => o.key === layout)?.description ?? ''}
        >
          {LAYOUT_OPTIONS.map((opt) => (
            <option key={opt.key} value={opt.key} title={opt.description}>
              {opt.label}
            </option>
          ))}
        </select>
      </div>

      {overrideCount > 0 && (
        <button
          type="button"
          onClick={() => clearLayoutPositions(layout)}
          className="flex items-center gap-1.5 rounded border border-border bg-card px-2 py-1 font-mono text-[11px] text-foreground hover:bg-muted"
          title={`Discard ${overrideCount} manual position${overrideCount === 1 ? '' : 's'} for the ${layout} layout — tiles snap back to the auto-computed positions.`}
        >
          Reset positions ({overrideCount})
        </button>
      )}

      <ToggleChip
        label="Hide skipped"
        checked={hideSkipped}
        onChange={setHideSkipped}
        title={
          'Hide systems that never executed in the selected tick range (skip rate ≥ 100%). '
          + 'Useful for isolating "what actually ran" — e.g. high-tier systems that only fire '
          + 'every 30/60 ticks drop out when you scope to a small window. No effect when no '
          + 'range is selected.'
        }
      />
      <ToggleChip
        label="Cross-phase edges"
        checked={showCrossPhaseEdges}
        onChange={setShowCrossPhaseEdges}
        title={
          isLaneLayout
            ? 'Show edges that span phase boundaries in the swim-lane layouts. Off by default '
              + 'because lane order already encodes phase ordering and the cross-phase chain is '
              + 'visually noisy.'
            : 'In compact / circular layouts every edge is already drawn — this toggle only '
              + 'affects the swim-lane layouts.'
        }
        muted={!isLaneLayout}
      />
      <ToggleChip
        label="Show engine tracks"
        checked={showEngineTracks}
        onChange={setShowEngineTracks}
        title={
          'Reveal the engine-internal tracks (Engine-Pre, Engine-Post / Fence). Off by default — '
          + "the engine's own DAGs are infrastructure noise for app-level work. When on, they "
          + 'render as their own delimited DAG groups. Keyed off the track’s `engine` tag.'
        }
      />

      <div className="ml-auto flex items-center gap-2 font-mono text-[10px] text-muted-foreground">
        <ParallelismPill utilization={utilization} />
        {tickRange ? (
          <>
            <span>
              Ticks {tickRange.from}–{tickRange.to}{' '}
              <span className="text-muted-foreground/60">({tickRange.to - tickRange.from + 1})</span>
            </span>
            <button
              type="button"
              onClick={onClearClick}
              className="rounded px-1.5 py-0.5 text-muted-foreground hover:bg-muted/40 hover:text-foreground"
              title="Clear the time selection — node stats hidden until you snapshot or scrub the profiler"
            >
              clear
            </button>
          </>
        ) : hasTimeSelection ? (
          // A selection IS set, but the window doesn't intersect any tick (e.g. scrubbed before the
          // first tick). Show a hint instead of a stale tick range.
          <span>Selection has no ticks — scrub or snapshot.</span>
        ) : (
          <span>No range — snapshot or scrub the profiler to enable stats.</span>
        )}
        {legendsVisible && (
          <button
            type="button"
            onClick={() => setHelpOpen((o) => !o)}
            className="flex h-5 w-5 items-center justify-center rounded-full border border-border bg-card text-foreground hover:bg-muted"
            title="Show controls + legend (toggle inline help with `l`)"
            aria-label="Show controls and legend"
          >
            ?
          </button>
        )}
      </div>
      {helpOpen && <HelpOverlay onClose={() => setHelpOpen(false)} />}
    </div>
  );
}

/**
 * Modal-ish overlay describing every control + visual element in the System DAG view. Portaled
 * to `document.body` so it escapes any dockview ancestor (transforms / overflow). Click-outside
 * + Escape close it.
 */
function HelpOverlay({ onClose }: { onClose: () => void }) {
  return createPortal(
    <div
      className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/60"
      onClick={onClose}
      onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
      role="dialog"
      aria-modal="true"
      tabIndex={-1}
    >
      <div
        className="max-h-[88vh] w-[840px] max-w-[94vw] overflow-auto rounded-lg border border-border bg-card p-6 font-mono text-[12px] leading-relaxed text-foreground shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-[15px] font-semibold">System DAG — controls & legend</h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded px-2 py-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            ✕
          </button>
        </div>

        <Section title="What you're looking at">
          <p>
            Every system the engine schedules, drawn as a tile, with arrows showing the dependencies that order them. The tiles
            colour-code <em>how slow</em> each system is on average; the arrows colour-code <em>why one runs before another</em>.
            Click a tile to open the side panel, which shows the system's declared component / event / resource access — the raw
            facts the engine derives the dependency graph from.
          </p>
          <p className="mt-2 text-muted-foreground">
            The DAG is a <strong>topology view</strong> (what runs after what), not a <strong>timeline</strong> (when it ran).
            Pair it with the Critical Path panel to see where time actually went on a given tick.
          </p>
        </Section>

        <Section title="Layouts">
          <ul className="list-disc space-y-1.5 pl-5">
            <li>
              <strong>Horizontal lanes</strong> — phases stack top-to-bottom (Input, Movement, Lifecycle, …); systems flow
              left-to-right within each phase. Coloured swim-lane bands behind the tiles make phase membership obvious at a glance.
              Best layout for understanding the per-tick lifecycle.
            </li>
            <li>
              <strong>Vertical lanes</strong> — same idea, rotated. Phases as side-by-side columns; systems flow top-to-bottom.
              Useful when the dock pane is taller than wide.
            </li>
            <li>
              <strong>Compact</strong> — flat layered layout, no swim-lanes. Cross-phase edges become visible. Good when you
              want to see the full dependency mesh without the phase scaffolding.
            </li>
            <li>
              <strong>Circular</strong> — systems on a circle, ordered by phase then name. All edges drawn. Niche; useful for
              spotting accidental cycles or visualising heavily-cross-phase event traffic.
            </li>
          </ul>
        </Section>

        <Section title="Node tile — what every visual layer means">
          <p className="mb-2">A single system tile has up to seven distinct visual signals stacked on it:</p>
          <ul className="list-disc space-y-2 pl-5">
            <li>
              <strong>Heat border</strong> — main border colour. Driven by the system's primary stat over the selected tick
              range. Cool blue (220°) at slow-end-of-zero up through green to red (0°) at the hottest. Hottest-third tiles also
              get a soft glow. <em>No range selected → no heat (default neutral border).</em>
              <div className="mt-1.5 flex items-center gap-3 text-muted-foreground">
                <span className="inline-flex items-center gap-1.5"><span className="inline-block h-3 w-3 rounded-sm border-2" style={{ borderColor: 'hsla(220, 70%, 55%, 0.5)' }} /> cool / fast</span>
                <span className="inline-flex items-center gap-1.5"><span className="inline-block h-3 w-3 rounded-sm border-2" style={{ borderColor: 'hsla(110, 70%, 55%, 0.7)' }} /> mid</span>
                <span className="inline-flex items-center gap-1.5"><span className="inline-block h-3 w-3 rounded-sm border-2" style={{ borderColor: 'hsla(0, 70%, 55%, 0.9)' }} /> hot / slow</span>
              </div>
            </li>
            <li>
              <strong>Stat chip</strong> (small µs / ms label, top-left of the tile body) — the system's actual aggregated
              duration in the selected range. Hover for the precise value.
            </li>
            <li>
              <strong>Kind chip</strong> (top-right) — system type:
              <span className="ml-1 inline-flex items-center gap-1 rounded bg-emerald-100 px-1 py-px text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200">PIPELINE</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-sky-100 px-1 py-px text-sky-800 dark:bg-sky-900/40 dark:text-sky-200">QUERY</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-violet-100 px-1 py-px text-violet-800 dark:bg-violet-900/40 dark:text-violet-200">CALLBACK</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-slate-200 px-1 py-px text-slate-800 dark:bg-slate-800 dark:text-slate-300">UNKNOWN</span>.
              Pipeline = chunked sequential, Query = ECS query (often parallel), Callback = single-shot.
            </li>
            <li>
              <strong>★ badge</strong> — critical-path participation rate. <em>Solid amber star</em> when the system is on the
              CP in ≥50 % of ticks in the range; <em>dim star</em> for 10–50 %; nothing below 10 %.
              Hover for the exact percentage.
            </li>
            <li>
              <strong>⌛ hourglass</strong> (small, top-right next to the kind chip) — the system has a non-trivial mean
              dispatch wait (≥ 10 µs from "all predecessors done" to "worker actually grabbed it") in the current range.
              Click the tile to see the gating predecessor in the side panel's <strong>Gated by</strong> section. If many
              tiles carry the icon, your scheduler is paying real fork-join overhead.
            </li>
            <li>
              <strong>Red outline</strong> (around the tile, slightly offset) — system is on the critical path of the
              <strong> dominant tick</strong> (the longest tick in the current range). Single-tick spotlight, distinct from the ★
              badge which is range-wide.
            </li>
            <li>
              <strong>Amber left bar</strong> (4 px on the left edge) — exclusive-phase system. The phase containing this system
              runs nothing else in parallel; useful for spotting accidental serialisation.
            </li>
            <li>
              <strong>Inline chips</strong> below the name — flags about the system:
              <ul className="mt-1 list-disc space-y-0.5 pl-5 text-muted-foreground">
                <li><strong>parallel</strong> — runs across multiple workers (chunked).</li>
                <li><strong>exclusive</strong> — same idea as the amber left bar, surfaced as a chip too.</li>
                <li><strong>tier N</strong> — only runs against entities at tier <code>N</code> (LOD / detail bucket).</li>
                <li><strong>change:N</strong> — has <code>N</code> change-filter declarations (only runs when those components changed).</li>
                <li><strong>↪ NN%</strong> — skip rate. Surfaced when {`>`} 50 %; means the system's <code>ShouldRun</code> /
                  <code>ReactiveSkip</code> bailed on the majority of ticks.</li>
                <li><strong>no decls</strong> — the system has no declared access. Common for <code>CallbackSystem</code>; not
                  necessarily a bug, but the engine has no way to derive dependencies for it.</li>
              </ul>
            </li>
            <li>
              <strong>Selection ring</strong> (theme primary colour, 2 px) — you've clicked the tile.
              <strong> Hover ring</strong> (neutral foreground at 60 %) — the cross-panel hover store says this system is being
              pointed at (here or in the CP tape).
            </li>
          </ul>
        </Section>

        <Section title="Edges — colour and dash code the kind of dependency">
          <p className="mb-2">Five edge kinds, each with a fixed colour + dash pattern:</p>
          <table className="w-full border-collapse">
            <thead>
              <tr className="border-b border-border text-left text-muted-foreground">
                <th className="py-1 pr-3 font-normal">kind</th>
                <th className="py-1 pr-3 font-normal">style</th>
                <th className="py-1 font-normal">means</th>
              </tr>
            </thead>
            <tbody>
              <tr className="border-b border-border/40 align-top">
                <td className="py-1.5 pr-3"><strong className="text-amber-400">fresh</strong></td>
                <td className="py-1.5 pr-3"><EdgeSwatch colour="#f59e0b" /></td>
                <td className="py-1.5">Solid amber. <code>Writes&lt;T&gt;</code> → another system's <code>ReadsFresh&lt;T&gt;</code>, same phase. The reader sees the writer's update.</td>
              </tr>
              <tr className="border-b border-border/40 align-top">
                <td className="py-1.5 pr-3"><strong className="text-sky-400">snapshot</strong></td>
                <td className="py-1.5 pr-3"><EdgeSwatch colour="#3b82f6" /></td>
                <td className="py-1.5">Solid blue. Snapshot reader runs <em>before</em> the writer (edge direction reverses): the reader gets the pre-tick value.</td>
              </tr>
              <tr className="border-b border-border/40 align-top">
                <td className="py-1.5 pr-3"><strong className="text-slate-400">manual</strong></td>
                <td className="py-1.5 pr-3"><EdgeSwatch colour="#94a3b8" /></td>
                <td className="py-1.5">Solid slate. Explicit <code>.After()</code> / <code>.Before()</code> ordering — the user pinned the order by hand, not derivable from access.</td>
              </tr>
              <tr className="border-b border-border/40 align-top">
                <td className="py-1.5 pr-3"><strong className="text-violet-400">event</strong></td>
                <td className="py-1.5 pr-3"><EdgeSwatch colour="#a78bfa" dashed /></td>
                <td className="py-1.5">Dashed violet. <code>WritesEvents&lt;Q&gt;</code> producer → <code>ReadsEvents&lt;Q&gt;</code> consumer. Crosses phase boundaries (events buffer between ticks). <em>Live styling overlay below.</em></td>
              </tr>
              <tr className="align-top">
                <td className="py-1.5 pr-3"><strong className="text-red-400">resource</strong></td>
                <td className="py-1.5 pr-3"><EdgeSwatch colour="#ef4444" dotted /></td>
                <td className="py-1.5">Dotted red. Two systems touching the same named resource where at least one writes — a conflict that forced an ordering.</td>
              </tr>
            </tbody>
          </table>
          <p className="mt-2 text-muted-foreground">The label on each arrow names the component / queue / resource that justifies it. If multiple, shows <code>firstName +N</code>.</p>
        </Section>

        <Section title="Event edges — live backpressure overlay">
          <p>
            When a tick range is selected, event edges (the dashed ones) get re-coloured by the queue's actual behaviour
            in the range. The dash pattern stays violet by default but the stroke shifts:
          </p>
          <ul className="mt-1 list-disc space-y-0.5 pl-5">
            <li><strong>Default violet</strong> — idle / no traffic.</li>
            <li><strong>Heating up to orange</strong> — peak depth growing. Hue ramps 270° (violet) → 30° (orange) with relative load.</li>
            <li><strong>Deep red + animated dashes</strong> — queue overflowed (events were dropped). Label gets a <span className="text-red-400">⚠ Nk drops</span> prefix.</li>
            <li><strong>Stroke width</strong> bumps up with chronic backlog (end-of-tick depth, separate channel from peak).</li>
          </ul>
          <p className="mt-1 text-muted-foreground">Two independent channels — colour answers "worst moment", width answers "always backlogged".</p>
        </Section>

        <Section title="Toolbar controls">
          <KeyTable rows={[
            ['Snapshot last 600 ticks', 'Pin both DAG aggregations and the profiler TimeArea to the most recent 600 ticks. Auto-fired once on first metadata arrival so a fresh open is useful without a click.'],
            ['stat: mean / p50 / p95 / p99 / max', 'Aggregation function for the per-system primary stat that drives the heat border + chip. mean = average duration; p99 = "the tick where this system was unusually slow"; max = worst single tick.'],
            ['layout', 'Pick one of the four layouts described above. Switching re-runs the layout engine and re-fits the viewport.'],
            ['☐ Hide skipped', 'Hide systems that contributed nothing to the selected range — ShouldRun-bailed every tick, tier-filtered out, or scheduled-but-zero-duration. Driven by the same data as the visible "0.0 µs" on the tile. Topology is filtered before layout so dagre packs the surviving systems tightly (no holes). No-op when no range is selected.'],
            ['☐ Cross-phase edges', 'Surface edges that span phase boundaries in the swim-lane layouts (otherwise hidden because lane order already encodes phase ordering). The current implementation gates them on hover: with the toggle on but no system pointed at, intra-phase edges still render alone; hover any system to reveal its cross-phase neighbours. Compact / circular layouts always show every edge — the chip dims to signal the no-op there.'],
            ['Ticks A–B (N)', 'Range read-out. Shows the tick window currently driving the stats. clear strips the time selection — node stats hide until you snapshot or scrub the profiler.'],
            ['?', 'Open this panel.'],
          ]} />
        </Section>

        <Section title="Mouse">
          <KeyTable rows={[
            ['Click node',          'Select system + open the side panel. Cross-panel binding means the CP tape pins focus to the same system.'],
            ['Click pane',          'Deselect. Closes side panel.'],
            ['Hover node',          'Cross-panel hover sync — matching bar in the CP tape lights up.'],
            ['Hover lane label',    'Hover sync at the phase level — matching CP tape phase stripe brightens.'],
            ['Drag pane',           'Pan the canvas.'],
            ['Ctrl + drag node',    'Move that tile manually — useful when an edge is hidden behind a system box. Position is persisted per-layout to localStorage and survives reloads. The toolbar shows a "Reset positions (N)" button while overrides exist for the current layout.'],
            ['Wheel',               'Zoom in / out.'],
            ['MiniMap (bottom-right)', 'Click / drag a region to navigate; shows the full canvas at a glance.'],
            ['Controls (bottom-left)', 'Buttons for fit-view + zoom in/out without scrolling.'],
          ]} />
        </Section>

        <Section title="Cross-panel sync">
          <p>The DAG shares state with the rest of the workbench via three slots:</p>
          <ul className="mt-1 list-disc space-y-1 pl-5">
            <li>
              <strong>Time window</strong> (<code>useSelectionStore.time</code>) — the µs range driving aggregations. Set by
              the Snapshot button here, by scrubbing the profiler's TimeArea, or by deep-linking via URL.
            </li>
            <li>
              <strong>Selected system</strong> (<code>useSelectionStore.system</code>) — the clicked system. Drives the side
              panel here, the bar selection in the CP tape, and the focus tick when applicable.
            </li>
            <li>
              <strong>Hovered system / phase</strong> (<code>useHoverStore</code>) — volatile, cleared on mouse-leave. Pure UI;
              not URL-synced. Drives the matching highlights between the DAG canvas and the CP tape.
            </li>
          </ul>
        </Section>

        <Section title="Side panel — declared access">
          <p>
            Clicking a tile opens the side panel. It shows the raw access declarations for the selected system: the
            <code>Reads</code> / <code>ReadsFresh</code> / <code>ReadsSnapshot</code> / <code>Writes</code> components, the
            <strong>event queues</strong> (separate <em>reads events</em> / <em>writes events</em> sections, violet to
            match the dashed event edges), the <strong>named resources</strong> (separate <em>reads resources</em> /
            <em>writes resources</em> sections, red to match the dotted resource edges), and any explicit
            <code>.After/.Before</code> ordering. This is the source of truth the engine derived the dependency graph
            from — if an arrow surprises you, the side panel will tell you which declaration forced it.
          </p>
        </Section>

        <Section title='Side panel — "Gated by" wait analysis'>
          <p>
            When a tick range is selected, the side panel adds a <strong>Gated by</strong> section that answers "why
            does this system wait, and what for?". Per predecessor it shows: who, what % of ticks they were the gating
            predecessor (the one whose <code>EndUs</code> matched this system's <code>ReadyUs</code>), how long that
            predecessor took on average, and the edge metadata (kind + via list) that justified the dependency in the
            first place.
          </p>
          <p className="mt-2">
            The top gater (≥ 50 % of ticks) is highlighted with an amber background. When that primary gater dominates,
            a sky-coloured <strong>Mitigation</strong> hint appears below the table, suggesting the typical fix for
            that edge kind:
          </p>
          <ul className="mt-1 list-disc space-y-0.5 pl-5 text-muted-foreground">
            <li><strong>fresh</strong> (incl. cross-phase W×W): if the conflicting write is conditional / rare, move it to a dedicated system or use <code>SideWrites&lt;T&gt;()</code>.</li>
            <li><strong>snapshot</strong>: if the previous-tick value isn't strictly required, downgrade to a fresh read to remove the inversion.</li>
            <li><strong>manual</strong>: verify the explicit <code>.After()</code> / <code>.Before()</code> is still required.</li>
            <li><strong>event</strong> / <strong>resource</strong>: no canned hint — these are usually intentional.</li>
          </ul>
          <p className="mt-2 text-muted-foreground">
            Math note: <code>ReadyUs == max(predecessor.EndUs)</code> by construction, so identifying the gater is
            exact, not estimated. If you don't see the gating-predecessor edge on the canvas, it's probably cross-phase
            — flip <em>Cross-phase edges</em> on and hover the system to reveal it.
          </p>
        </Section>

        <Section title="Canvas — gating-edge highlight">
          <p>
            Selecting a tile bolds the edge from its top gating predecessor (heavier stroke, drop-shadow glow,
            animated dashes). The kind colour stays the same — only the prominence changes — so the canvas mirrors the
            "Gated by …" answer in the side panel. With nothing selected, all edges render at their default weight.
          </p>
        </Section>

        <Section title="When the DAG is empty">
          <ul className="list-disc space-y-1 pl-5">
            <li><strong>"No topology yet"</strong> — open a trace or attach a session. The DAG is built from the topology, which the engine ships once at startup.</li>
            <li><strong>Tiles render but no heat colour</strong> — no time range selected. Snapshot here or scrub the profiler.</li>
            <li><strong>"no decls" chip on most tiles</strong> — the engine isn't surfacing RFC 07 access declarations on the wire; arrows are derived only from explicit <code>.After/.Before</code>. Common for old traces / pre-RFC-07 codebases.</li>
          </ul>
        </Section>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mb-5">
      <h3 className="mb-2 text-[12px] font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      <div className="space-y-1 leading-relaxed">{children}</div>
    </div>
  );
}

function KeyTable({ rows }: { rows: Array<[string, string]> }) {
  return (
    <table className="w-full">
      <tbody>
        {rows.map(([k, v]) => (
          <tr key={k} className="align-top">
            <td className="w-52 py-0.5 pr-3"><kbd className="rounded border border-border bg-muted px-1.5 py-0.5 text-[10px]">{k}</kbd></td>
            <td className="py-0.5 text-muted-foreground">{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * Small checkbox-style chip for boolean view toggles in the toolbar. Native checkbox + label so
 * keyboard / accessibility behave correctly; the chip styling matches the surrounding toolbar
 * controls (mono 11px, bordered, hoverable). The `muted` flag dims the chip when the toggle has
 * no effect under the current layout — the click still works, but the user gets a visual hint
 * that they need to switch layout to see the result.
 */
function ToggleChip({
  label,
  checked,
  onChange,
  title,
  muted = false,
}: {
  label: string;
  checked: boolean;
  onChange: (next: boolean) => void;
  title: string;
  muted?: boolean;
}) {
  const tone = muted
    ? 'border-border bg-card text-muted-foreground/70 hover:bg-muted'
    : 'border-border bg-card text-foreground hover:bg-muted';
  return (
    <label
      className={`flex cursor-pointer select-none items-center gap-1.5 rounded border px-2 py-1 font-mono text-[11px] ${tone}`}
      title={title}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="h-3 w-3 cursor-pointer accent-primary"
      />
      {label}
    </label>
  );
}

/** Tiny inline edge sample — coloured line with optional dash / dot pattern, matching the actual edge styling on the canvas. */
function EdgeSwatch({ colour, dashed = false, dotted = false }: { colour: string; dashed?: boolean; dotted?: boolean }) {
  const dasharray = dashed ? '6 4' : dotted ? '2 4' : undefined;
  return (
    <svg width="64" height="10" className="inline-block align-middle">
      <line x1="0" y1="5" x2="64" y2="5" stroke={colour} strokeWidth="2" strokeDasharray={dasharray} />
    </svg>
  );
}
