import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { Camera } from 'lucide-react';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { lastNTicksToTime, timeToTickRange } from './tickRangeMapping';
import { useDagViewStore, type LayoutMode, type StatMode } from './useDagViewStore';

interface Props {
  /** Profiler-metadata tick rows — used for both the µs↔tick conversion and the auto-snapshot decision. */
  tickSummaries: readonly TickSummaryDto[] | null;
  /** Auto-snapshot once on first arrival of metadata when no time selection exists yet. */
  autoSnapshotEnabled: boolean;
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
export default function SystemDagToolbar({ tickSummaries, autoSnapshotEnabled }: Props) {
  const time = useSelectionStore((s) => s.time);
  const setTime = useSelectionStore((s) => s.setTime);
  const statMode = useDagViewStore((s) => s.statMode);
  const setStatMode = useDagViewStore((s) => s.setStatMode);
  const layout = useDagViewStore((s) => s.layout);
  const setLayout = useDagViewStore((s) => s.setLayout);
  // Help glyph follows the app-wide `legendsVisible` flag (toggled via the `l` key or the
  // "Toggle Legends" palette command). Hidden when legends are off so chrome stays minimal.
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);
  const [helpOpen, setHelpOpen] = useState(false);

  const hasTicks = tickSummaries != null && tickSummaries.length > 0;

  // Translate the current time-window (µs) back to ticks for the readout. Cross-panel scrubs
  // round through the converter so the user sees consistent tick numbers regardless of who set
  // the window.
  const tickRange = useMemo(() => timeToTickRange(time, tickSummaries), [time, tickSummaries]);

  // Auto-snapshot once on first arrival when the time slot is unset. After that, snapshot is
  // user-driven (per §7.3 — no continuous live updates).
  useEffect(() => {
    if (!autoSnapshotEnabled) return;
    if (!hasTicks || time != null || tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) setTime(next);
  }, [autoSnapshotEnabled, hasTicks, time, tickSummaries, setTime]);

  const onSnapshotClick = () => {
    if (tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) setTime(next);
  };

  const onClearClick = () => setTime(null);

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

      <div className="ml-auto flex items-center gap-2 font-mono text-[10px] text-muted-foreground">
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
        ) : time != null ? (
          // Time slot is set but the window doesn't intersect any tick (e.g. selection scrubbed
          // before the first tick). Show a hint instead of a stale tick range.
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
              <span className="ml-1 inline-flex items-center gap-1 rounded bg-emerald-900/40 px-1 py-px text-emerald-200">PIPELINE</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-sky-900/40 px-1 py-px text-sky-200">QUERY</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-violet-900/40 px-1 py-px text-violet-200">CALLBACK</span>{' '}
              <span className="inline-flex items-center gap-1 rounded bg-slate-800 px-1 py-px text-slate-300">UNKNOWN</span>.
              Pipeline = chunked sequential, Query = ECS query (often parallel), Callback = single-shot.
            </li>
            <li>
              <strong>★ badge</strong> — critical-path participation rate. <em>Solid amber star</em> when the system is on the
              CP in ≥50 % of ticks in the range; <em>dim star</em> for 10–50 %; nothing below 10 %.
              Hover for the exact percentage.
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
                <li><strong>↪ NN%</strong> — skip rate. Surfaced when {`>`} 50 %; means the system's <code>RunIf</code> /
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
            event queues it produces / consumes, the resources it touches, the explicit <code>.After/.Before</code> ordering
            it pinned, plus its phase, priority, parallelism mode, and tier filter. This is the source of truth the engine
            derived the dependency graph from — if an arrow surprises you, the side panel will tell you which declaration
            forced it.
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

/** Tiny inline edge sample — coloured line with optional dash / dot pattern, matching the actual edge styling on the canvas. */
function EdgeSwatch({ colour, dashed = false, dotted = false }: { colour: string; dashed?: boolean; dotted?: boolean }) {
  const dasharray = dashed ? '6 4' : dotted ? '2 4' : undefined;
  return (
    <svg width="64" height="10" className="inline-block align-middle">
      <line x1="0" y1="5" x2="64" y2="5" stroke={colour} strokeWidth="2" strokeDasharray={dasharray} />
    </svg>
  );
}
