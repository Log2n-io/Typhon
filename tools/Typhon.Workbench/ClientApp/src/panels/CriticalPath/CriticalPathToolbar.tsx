import { useState } from 'react';
import { createPortal } from 'react-dom';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import type { TickPathBars } from './criticalPath';
import { useCriticalPathViewStore, type Orientation } from './useCriticalPathViewStore';

interface Props {
  bars: TickPathBars | null;
  onFit: () => void;
}

/**
 * Top strip for the dedicated Critical-Path view. Carries the tick label, orientation +
 * scale toggles, zoom indicator, and a Fit button. All view-state lives in
 * {@link useCriticalPathViewStore} so the toggles are dumb pass-throughs.
 */
export default function CriticalPathToolbar({ bars, onFit }: Props) {
  const orientation = useCriticalPathViewStore((s) => s.orientation);
  const setOrientation = useCriticalPathViewStore((s) => s.setOrientation);
  const pxPerUs = useCriticalPathViewStore((s) => s.pxPerUs);
  const lockZoom = useCriticalPathViewStore((s) => s.lockZoom);
  const setLockZoom = useCriticalPathViewStore((s) => s.setLockZoom);
  const fullGantt = useCriticalPathViewStore((s) => s.fullGantt);
  const setFullGantt = useCriticalPathViewStore((s) => s.setFullGantt);
  const aggregateMode = useCriticalPathViewStore((s) => s.aggregateMode);
  const setAggregateMode = useCriticalPathViewStore((s) => s.setAggregateMode);
  const showMetronome = useCriticalPathViewStore((s) => s.showMetronome);
  const setShowMetronome = useCriticalPathViewStore((s) => s.setShowMetronome);
  // Help glyph follows the app-wide `legendsVisible` flag (toggled via the `l` key or the
  // "Toggle Legends" palette command). Hidden when legends are off so chrome stays minimal.
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);
  const [helpOpen, setHelpOpen] = useState(false);

  const isFallback = bars?.mode === 'execution-order';

  return (
    <div className="flex items-center gap-3 border-b border-border bg-background/80 px-3 py-1.5 font-mono text-[11px]">
      <span className="font-semibold text-foreground">
        {bars
          ? bars.aggregate
            ? `Aggregate · ${bars.aggregate.tickCount} ticks`
            : `Tick ${bars.tickNumber}`
          : 'Critical path'}
      </span>
      {isFallback && (
        <span
          className="rounded bg-amber-950/50 px-1 py-px text-[9px] uppercase text-amber-300"
          title="Topology has no RFC 07 access declarations — bars are sorted by startUs, not by data-flow path."
        >
          execution order
        </span>
      )}
      {bars && <span className="text-muted-foreground">{formatUs(bars.totalUs)}</span>}

      <div className="ml-auto flex items-center gap-3">
        <SegmentedControl<'single' | 'aggregate'>
          label="mode"
          value={aggregateMode ? 'aggregate' : 'single'}
          options={['single', 'aggregate']}
          onChange={(v) => setAggregateMode(v === 'aggregate')}
        />
        <SegmentedControl<Orientation>
          label="orientation"
          value={orientation}
          options={['auto', 'horizontal', 'vertical']}
          onChange={setOrientation}
        />
        <label
          className="flex cursor-pointer items-center gap-1 text-muted-foreground"
          title="Append every system that ran (not just the critical path) — non-CP bars render dimmed alongside CP bars. Off by default; flip on to investigate 'what else was running'."
        >
          <input
            type="checkbox"
            checked={fullGantt}
            onChange={(e) => setFullGantt(e.target.checked)}
            className="h-3 w-3"
          />
          full Gantt
        </label>
        <label
          className="flex cursor-pointer items-center gap-1 text-muted-foreground"
          title="Show the leading metronome-wait stripe — the gap from the previous TickEnd to this TickStart. Off by default per spec; flip on to investigate engine throttling / sleep behaviour. The intent-class chip (Headroom / Throttled / CatchUp) is rendered on the stripe when there's room."
        >
          <input
            type="checkbox"
            checked={showMetronome}
            onChange={(e) => setShowMetronome(e.target.checked)}
            className="h-3 w-3"
          />
          metronome
        </label>
        <span className="text-muted-foreground">{formatZoom(pxPerUs)}</span>
        <label
          className="flex cursor-pointer items-center gap-1 text-muted-foreground"
          title="When unchecked, the view auto-fits whenever the displayed tick changes. Check to preserve your manual zoom across tick scrubs (handy for comparing the same phase or system across many ticks)."
        >
          <input
            type="checkbox"
            checked={lockZoom}
            onChange={(e) => setLockZoom(e.target.checked)}
            className="h-3 w-3"
          />
          lock zoom
        </label>
        <button
          type="button"
          onClick={onFit}
          className="rounded border border-border bg-card px-2 py-0.5 text-foreground hover:bg-muted"
          title="Fit timeline to viewport (or press 0, or middle-click the canvas)"
        >
          fit
        </button>
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
 * Modal-ish overlay describing every control + visual element in the Critical-Path view. Portaled
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
        className="max-h-[80vh] w-[640px] max-w-[90vw] overflow-auto rounded-lg border border-border bg-card p-5 font-mono text-[11px] text-foreground shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-[13px] font-semibold">Critical Path — controls & legend</h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded px-2 py-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            ✕
          </button>
        </div>

        <Section title="What you're looking at">
          <p>One representative tick of the engine, flattened into a single timeline. Each system that ran on the critical path is a coloured bar; waits between systems show as hatched segments. The phase header band above the bars marks which phase each stretch belongs to (5 px coloured stripe + name). Post-tick serial work appears at the trailing edge.</p>
          <p className="mt-1 text-muted-foreground">When the topology has no RFC 07 access declarations, the walker can't trace a real critical path — the view falls back to "execution order" (every system that ran, sorted by <code>startUs</code>) and shows an amber pill in the header.</p>
        </Section>

        <Section title="Mouse">
          <KeyTable rows={[
            ['Wheel',                     'Zoom in / out, anchored under the cursor'],
            ['Shift + Wheel',             'Scroll along the time axis (5× step)'],
            ['Ctrl + Wheel',              'Scroll perpendicular to the time axis'],
            ['Horizontal wheel / tilt',   'Scroll horizontally'],
            ['Middle-click',              'Fit timeline to viewport (work + post-tick)'],
            ['Click a system bar',        'Select system + pin focus tick'],
            ['Hover',                     'Tooltip: phase, duration, wait classes, parallelism'],
          ]} />
        </Section>

        <Section title="Keyboard">
          <KeyTable rows={[
            ['+ / =',  'Zoom in (1.25×)'],
            ['- / _',  'Zoom out (1 / 1.25)'],
            ['0',      'Fit timeline to viewport'],
            ['l',      'Toggle inline help (this button + future legends) — app-wide'],
          ]} />
        </Section>

        <Section title="Colour / shape legend">
          <ul className="list-disc space-y-1 pl-5">
            <li><strong>Phase header stripe</strong> (5 px above the bars) — phase identity. Stable colour per phase across ticks.</li>
            <li><strong>System bars</strong> — coloured by a stable hash of <code>systemName</code> using the profiler's 13-colour Turbo ramp (same one used for Cache Fetch / Allocate / Evicted / WAL / Checkpoint). Same system always gets the same hue. Bar text contrast is picked per-bar via WCAG luminance.</li>
            <li><strong>Hatched segments</strong> — waits. <em>Worker-claim</em> (between deps cleared and a worker picking up), <em>chunk-dispatch</em> (work-stealing imbalance), <em>phase-fence</em> (waiting for the slowest non-CP system to clear the fence). Hover to see which.</li>
            <li><strong>Leading hatched stripe</strong> — metronome wait, the gap from the previous TickEnd to this TickStart. Excluded from "Fit" so the work itself fills the viewport; scroll left to see it.</li>
            <li><strong>Post-tick serial</strong> — trailing block: WriteTickFence, WAL flush, TierBudget, etc.</li>
          </ul>
        </Section>

        <Section title="Tick source">
          <p>The displayed tick is the dominant tick (longest <code>durationUs</code>) inside the selected time window — or, when the window is narrower than any tick, the tick whose body contains the window's midpoint. Click a tick in the profiler's TickOverview or a bar here to pin a specific tick (snaps the viewport to that tick's bounds).</p>
        </Section>

        <Section title="Lock zoom">
          <p>By default, the view auto-fits whenever the displayed tick changes — fresh tick → fresh wall-clock total → previous zoom is the wrong scale. Check <strong>lock zoom</strong> in the toolbar to preserve your manual zoom across tick scrubs (handy for comparing the same phase / system across many ticks at the same scale).</p>
        </Section>
      </div>
    </div>,
    document.body,
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mb-4">
      <h3 className="mb-1.5 text-[12px] font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      <div className="space-y-1 leading-snug">{children}</div>
    </div>
  );
}

function KeyTable({ rows }: { rows: Array<[string, string]> }) {
  return (
    <table className="w-full">
      <tbody>
        {rows.map(([k, v]) => (
          <tr key={k} className="align-top">
            <td className="w-44 py-0.5 pr-3"><kbd className="rounded border border-border bg-muted px-1.5 py-0.5 text-[10px]">{k}</kbd></td>
            <td className="py-0.5 text-muted-foreground">{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function SegmentedControl<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: readonly T[];
  onChange: (v: T) => void;
}) {
  return (
    <div className="flex items-center gap-1">
      <span className="text-muted-foreground">{label}</span>
      <div className="flex overflow-hidden rounded border border-border">
        {options.map((opt) => {
          const active = value === opt;
          return (
            <button
              key={opt}
              type="button"
              onClick={() => onChange(opt)}
              className={`px-1.5 py-0.5 text-[10px] ${active ? 'bg-primary text-primary-foreground' : 'bg-card text-foreground hover:bg-muted'}`}
            >
              {opt}
            </button>
          );
        })}
      </div>
    </div>
  );
}

function formatUs(us: number): string {
  if (us < 1) return '0µs';
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}

function formatZoom(pxPerUs: number): string {
  // Default zoom = 0.05 px/µs (50 px/ms) → "1.0×" for user-friendly display. Scale relative to
  // that so common values (×1, ×2, ×8) read naturally.
  const ratio = pxPerUs / 0.05;
  if (ratio < 0.1) return `${ratio.toFixed(2)}×`;
  if (ratio < 10) return `${ratio.toFixed(1)}×`;
  return `${Math.round(ratio)}×`;
}
