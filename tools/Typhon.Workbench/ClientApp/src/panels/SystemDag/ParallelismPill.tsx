import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { RangeUtilization, TickUtilization } from './tickUtilization';

interface Props {
  utilization: RangeUtilization | null;
}

/**
 * A1 + A6 — toolbar pill that surfaces parallelism inefficiency over the selected tick range.
 *
 * **Headline (A1).** `Wait X.Xms · YY%` where Y% is `1 - work/(workerCount × wallTime)` weighted
 * across the range. Colour ramps green (≤ 25 % wait) → amber (25–50 %) → red (> 50 %).
 *
 * **Hover popover (A6).** Per-tick utilization sparkline + the formula breakdown so the user
 * can see whether the wait is constant or driven by a few outlier ticks.
 *
 * Suppressed when the input is `null` (worker count unknown, no rows in range, etc.) — the pill
 * disappears rather than showing a meaningless `0%`.
 */
export default function ParallelismPill({ utilization }: Props) {
  const [hoverEl, setHoverEl] = useState<HTMLDivElement | null>(null);

  if (!utilization || utilization.perTick.length === 0) return null;

  const waitPct = utilization.meanWaitFraction * 100;
  const tone =
    waitPct <= 25 ? 'good' :
    waitPct <= 50 ? 'warn' :
    'bad';
  // Theme-paired classes: light theme gets a soft tinted background + dark text; dark theme keeps
  // the existing deep-bg / light-text pairing. Without the `dark:` split, the dark-theme classes
  // (bg-*-950/40 / text-*-200) wash out to near-invisible on the white light theme.
  const toneClass =
    tone === 'good'
      ? 'border-emerald-300 bg-emerald-100 text-emerald-800 dark:border-emerald-700/50 dark:bg-emerald-950/40 dark:text-emerald-200'
      : tone === 'warn'
        ? 'border-amber-300 bg-amber-100 text-amber-800 dark:border-amber-700/50 dark:bg-amber-950/40 dark:text-amber-200'
        : 'border-red-300 bg-red-100 text-red-800 dark:border-red-700/60 dark:bg-red-950/40 dark:text-red-200';

  return (
    <>
      <div
        ref={setHoverEl}
        className={`flex items-center gap-1.5 rounded border px-1.5 py-0.5 font-mono text-fs-xs ${toneClass}`}
        title="Parallelism inefficiency over the selected range — hover for breakdown"
      >
        <span className="font-semibold">Wait</span>
        <span className="tabular-nums">{formatWaitMs(utilization.meanWaitUsPerTick)}</span>
        <span className="opacity-70">·</span>
        <span className="tabular-nums">{waitPct.toFixed(1)}%</span>
      </div>
      {hoverEl && <UtilizationPopover anchor={hoverEl} utilization={utilization} />}
    </>
  );
}

/**
 * Hover-only popover anchored to the pill. Uses a portal so dockview ancestors with overflow
 * clipping or transforms don't squish it. Positioning is viewport-fixed so the popover is the
 * same regardless of nested scroll.
 *
 * Visibility is gated by a CSS hover bridge on the anchor: we mount the portal unconditionally
 * but it only displays when the user actually hovers the anchor — cheap, no listener juggling.
 */
function UtilizationPopover({ anchor, utilization }: { anchor: HTMLDivElement; utilization: RangeUtilization }) {
  const [show, setShow] = useState(false);

  useEffect(() => {
    const onEnter = () => setShow(true);
    const onLeave = () => setShow(false);
    anchor.addEventListener('mouseenter', onEnter);
    anchor.addEventListener('mouseleave', onLeave);
    return () => {
      anchor.removeEventListener('mouseenter', onEnter);
      anchor.removeEventListener('mouseleave', onLeave);
    };
  }, [anchor]);

  if (!show) return null;
  const rect = anchor.getBoundingClientRect();

  // Anchor below the pill; flip to above if it would overflow the viewport bottom.
  const POPOVER_W = 340;
  const POPOVER_H = 180;
  let left = rect.left;
  let top = rect.bottom + 6;
  if (left + POPOVER_W > window.innerWidth) left = window.innerWidth - POPOVER_W - 8;
  if (top + POPOVER_H > window.innerHeight) top = rect.top - POPOVER_H - 6;

  return createPortal(
    <div
      className="pointer-events-none fixed z-[1000] rounded border border-border bg-card p-3 font-mono text-fs-xs text-foreground shadow-lg"
      style={{ left, top, width: POPOVER_W }}
    >
      <div className="mb-2 text-fs-sm font-semibold text-foreground">Parallelism inefficiency</div>
      <div className="grid grid-cols-2 gap-x-3 gap-y-0.5 text-muted-foreground">
        <span>workers</span>
        <span className="tabular-nums text-foreground">{utilization.workerCount}</span>
        <span>ticks</span>
        <span className="tabular-nums text-foreground">{utilization.perTick.length}</span>
        <span>mean util</span>
        <span className="tabular-nums text-foreground">{(utilization.meanUtilization * 100).toFixed(1)}%</span>
        <span>mean wait</span>
        <span className="tabular-nums text-foreground">{(utilization.meanWaitFraction * 100).toFixed(1)}%</span>
        <span>wait / tick</span>
        <span className="tabular-nums text-foreground">{formatWaitMs(utilization.meanWaitUsPerTick)}</span>
        <span>total wait</span>
        <span className="tabular-nums text-foreground">{formatWaitMs(utilization.totalWaitUs)}</span>
      </div>
      <div className="mt-2.5 mb-1 text-fs-2xs uppercase tracking-wide text-muted-foreground">per-tick wait %</div>
      <Sparkline perTick={utilization.perTick} />
      <div className="mt-1.5 text-fs-2xs leading-tight text-muted-foreground">
        wait = workers × wall − Σwork. Higher means more workers were idle while the tick advanced.
      </div>
    </div>,
    document.body,
  );
}

/**
 * Per-tick wait-fraction sparkline. SVG bar chart where bar height encodes wait fraction
 * ([0, 1] mapped to bar height) and bar colour is the same green/amber/red ramp as the pill
 * tone. Hovering individual bars is intentionally not wired — the popover already disappears
 * on mouse-leave so a nested hover would fight the parent's lifecycle. If per-tick drill-down
 * is wanted later, that's a click-pinned card, not a hover.
 */
function Sparkline({ perTick }: { perTick: TickUtilization[] }) {
  const W = 316;
  const H = 48;
  const PAD = 1;
  // Cap at 1.0 — utilization is already saturated to ≤ 1 in the computation, this is just for
  // the height calc.
  const wMax = 1;
  const barW = perTick.length > 0 ? Math.max(1, (W - PAD * 2) / perTick.length) : 0;
  return (
    <svg width={W} height={H} className="block">
      <rect x={0} y={0} width={W} height={H} fill="hsl(var(--muted))" opacity={0.2} />
      {perTick.map((t, i) => {
        const wf = 1 - t.utilization; // wait fraction — what the bar represents
        const h = Math.max(1, (wf / wMax) * (H - PAD * 2));
        const x = PAD + i * barW;
        const y = H - PAD - h;
        const fill = barFill(wf);
        return <rect key={t.tickNumber} x={x} y={y} width={Math.max(1, barW - 0.5)} height={h} fill={fill} />;
      })}
      {/* 50 % line — anything above is "more than half the worker-time was idle". */}
      <line x1={0} y1={H / 2} x2={W} y2={H / 2} stroke="hsl(var(--border))" strokeDasharray="2 3" strokeWidth={0.5} />
    </svg>
  );
}

function barFill(waitFraction: number): string {
  if (waitFraction <= 0.25) return 'hsl(142, 65%, 45%)';
  if (waitFraction <= 0.5) return 'hsl(38, 80%, 55%)';
  return 'hsl(0, 70%, 55%)';
}

function formatWaitMs(us: number): string {
  if (us < 1) return '0µs';
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  if (ms < 100) return `${ms.toFixed(1)}ms`;
  return `${Math.round(ms)}ms`;
}
