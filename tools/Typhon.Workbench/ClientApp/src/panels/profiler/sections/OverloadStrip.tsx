import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Expand, X } from 'lucide-react';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { setupCanvas } from '@/libs/profiler/canvas/canvasUtils';
import { getStudioThemeTokens } from '@/libs/profiler/canvas/theme';
import {
  HELP_GLYPH_MARGIN_RIGHT,
  HELP_GLYPH_Y_BASELINE,
  HELP_ICON_GLYPH_WIDTH,
  isInHelpHitZone,
} from '@/libs/profiler/canvas/tickOverview';
import { HelpOverlay } from '@/panels/profiler/components/HelpOverlay';

/**
 * Overload diagnostics strip — issue #289 follow-up.
 *
 * <p>Auto-hidden ribbon directly under <c>TickOverview</c>. Surfaces, per tick:
 * <ul>
 *   <li><b>overrunRatio</b> = <c>actualMs / targetMs</c> as a vertical bar — taller bars = more
 *       overrun. Reference horizontal lines drawn at the OverloadDetector escalation threshold
 *       (1.20×) and the engine target (1.00×).</li>
 *   <li><b>tickMultiplier</b> as bar colour using the same amber → red ramp <c>TickOverview</c>
 *       uses for its bar tint, so a glance ties multiplier (here) to throttle (in TickOverview).</li>
 * </ul>
 *
 * <p><b>Auto-hide rule.</b> The strip occupies zero vertical space when every tick has
 * <c>multiplier &lt;= 1</c> AND <c>overrunRatio &lt; 1.0</c> — i.e. the engine has stayed inside its
 * budget the whole trace and there is nothing to diagnose. The first time the trace exhibits an
 * overrun (or any throttle), the strip materialises automatically.
 *
 * <p><b>Sparkline, not aligned scroll.</b> The strip compresses ALL ticks into the available width
 * (no pan/scroll). The expand button ("⛶") opens a 5× taller popup with a scrollbar that gives
 * each tick at least {@link MIN_TICK_PX} pixels so individual events can be inspected.
 */
const OVERLOAD_HELP_LINES: string[] = [
  'Overload diagnostics strip',
  '',
  'Why it appears:',
  '  Visible only when at least one tick has multiplier > 1 OR',
  '  overrunRatio ≥ 1.0. A clean trace = strip stays hidden.',
  '',
  'What it shows (per tick, one column):',
  '',
  '  Bar height — overrunRatio = actualMs / targetMs',
  '    targetMs = 1000 / BaseTickRate (engine target at 1× rate).',
  '    1.0× means the tick took exactly its budget.',
  '    Y-axis caps at 2.0× — anything past that clamps to the top.',
  '',
  '  Bar colour — tick multiplier (engine throttle state)',
  '    1×  default — bar uses ratio-based hue (neutral / amber / red)',
  '    2×  amber              — engine slowed itself once',
  '    3×  orange             — second escalation step',
  '    4×  red                — third step (your migration-storm zone)',
  '    6×  dark red           — engine pinned at MinTickRateHz floor',
  '',
  '  Reference lines (dashed)',
  '    1.0× — the per-tick budget',
  '    1.2× — OverloadDetector escalation threshold',
  '         (5 consecutive ticks above this triggers throttle escalation)',
  '',
  'Intent classes (in tooltips):',
  '  CatchUp   — metronome target was already past at wait start; the',
  '              engine fired the next tick immediately (no real wait)',
  '  Throttled — multiplier > 1; engine voluntarily waited longer',
  '              between ticks to give itself headroom',
  '  Headroom  — multiplier == 1; normal idle waiting for the next',
  '              60 Hz boundary',
  '',
  'Hover any column for that tick\'s ratio, multiplier, level,',
  'pre-tick metronome wait, and the OverloadDetector streak counter:',
  '',
  '  Streak overrun: N / 5    (escalate at 5)',
  '    Consecutive ticks with overrunRatio > 1.2. Resets to 0 on',
  '    any non-overrun tick. Reaches 5 → multiplier escalates.',
  '',
  '  Streak underrun: N / 20  (deescalate at 20)',
  '    Consecutive ticks with overrunRatio < 0.6. Resets to 0 on',
  '    any overrun (a single tick > 1.2× breaks the streak).',
  '    Reaches 20 → multiplier deescalates one step.',
  '',
  '    Watch the streak: if it climbs to 18-19 then resets, your',
  '    workload has a periodic spike preventing deescalation.',
  '    Note: ticks in the 0.6×-1.2× dead-zone preserve the counter',
  '    (no climb, no reset) — only sub-0.6× ticks advance it.',
  '',
  'Move to TickOverview above to find the same tick by colour and',
  'click into TimeArea for span detail.',
  '',
  'Source data:',
  '  TickSummary.{OverloadLevel, TickMultiplier, MetronomeWaitUs,',
  '  MetronomeIntentClass} — written by IncrementalCacheBuilder',
  '  from the engine\'s TickEnd payload + observed Metronome.Wait',
  '  spans (kind 241). Cache version v10+.',
  '',
  'Press \'l\' to toggle help glyphs.',
];

const STRIP_HEIGHT = 32;
const POPUP_CANVAS_HEIGHT = STRIP_HEIGHT * 5; // 160 px
const POPUP_HEADER_HEIGHT = 24;
const POPUP_TOTAL_HEIGHT = POPUP_CANVAS_HEIGHT + POPUP_HEADER_HEIGHT;
// Minimum pixels per tick in scrollable mode — scrollbar appears when rows.length * MIN_TICK_PX
// exceeds the container width, giving the user per-tick resolution instead of a compressed sparkline.
const MIN_TICK_PX = 3;

// ─── row type ──────────────────────────────────────────────────────────────

interface OverloadRow {
  tickNumber: number;
  ratio: number;
  multiplier: number;
  level: number;
  waitUs: number;
  intent: number;
  durationUs: number;
  consecOver: number;
  consecUnder: number;
}


// ─── OverloadCanvas ─────────────────────────────────────────────────────────

interface OverloadCanvasProps {
  rows: OverloadRow[];
  targetMsForBaseRate: number;
  /** When true the inner div is at least rows.length * MIN_TICK_PX wide; overflow scrolls. */
  scrollable: boolean;
  showExpandButton: boolean;
  onExpandClick?: (rect: DOMRect) => void;
}

function OverloadCanvas({ rows, targetMsForBaseRate, scrollable, showExpandButton, onExpandClick }: OverloadCanvasProps): React.JSX.Element {
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);
  useThemeStore((s) => s.theme); // subscribe so the component re-renders on theme switch

  const canvasRef   = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const innerDivRef  = useRef<HTMLDivElement>(null); // only used when scrollable=true

  // Stable refs — let callbacks read current values without needing them as deps.
  const rowsRef     = useRef(rows);               rowsRef.current     = rows;
  const targetMsRef = useRef(targetMsForBaseRate); targetMsRef.current = targetMsForBaseRate;
  const legendsRef  = useRef(legendsVisible);      legendsRef.current  = legendsVisible;

  const helpHoveredRef = useRef(false);

  const [tickTooltip, setTickTooltip] = useState<{ lines: readonly string[]; clientX: number; clientY: number } | null>(null);
  const [helpTooltip, setHelpTooltip] = useState<{ clientX: number; clientY: number } | null>(null);
  const [cursor, setCursor] = useState<string>('crosshair');

  const draw = useCallback(() => {
    const canvas = canvasRef.current;
    const rs = rowsRef.current;
    if (!canvas || rs.length === 0) return;

    // In scrollable mode: pin the inner div to at least (rows × MIN_TICK_PX) pixels before
    // setupCanvas measures the canvas. getBoundingClientRect() inside setupCanvas forces a
    // synchronous layout, so the width is applied before we read it.
    if (scrollable && innerDivRef.current && containerRef.current) {
      const containerW = containerRef.current.getBoundingClientRect().width;
      const sw = Math.max(containerW, rs.length * MIN_TICK_PX);
      innerDivRef.current.style.width = `${sw}px`;
    }

    const { width, height } = setupCanvas(canvas);
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const theme = getStudioThemeTokens();
    const lv = legendsRef.current;

    ctx.fillStyle = theme.card;
    ctx.fillRect(0, 0, width, height);

    const yMax = 2.0;
    const yRatio = (r: number) => height - 1 - (Math.min(r, yMax) / yMax) * (height - 2);

    ctx.strokeStyle = theme.mutedForeground;
    ctx.lineWidth = 0.5;
    ctx.setLineDash([3, 3]);
    for (const r of [1.0, 1.2]) {
      const y = yRatio(r);
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke();
    }
    ctx.setLineDash([]);

    const colWidth = Math.max(1, width / rs.length);
    for (let i = 0; i < rs.length; i++) {
      const r = rs[i];
      const x = (i / rs.length) * width;
      const top = yRatio(r.ratio);
      const colour = multiplierTint(r.multiplier) ?? (r.ratio >= 1.2 ? '#dc2626' : r.ratio >= 1.0 ? '#f59e0b' : theme.overviewBar);
      ctx.fillStyle = colour;
      ctx.fillRect(Math.floor(x), Math.floor(top), Math.max(1, Math.floor(colWidth)), Math.ceil(height - 1 - top));
    }

    // Left-pinned labels — offset by scrollLeft so they stay visible when the canvas is scrolled.
    // In scrollable mode, push the label right by at least one tick-width when scrollX ≈ 0 so
    // that tick 0 is not obscured by the backdrop.  Once the user scrolls past tick 0 the offset
    // collapses back to the normal 2 px margin.
    const scrollX = containerRef.current?.scrollLeft ?? 0;
    const pinOffset = scrollable ? Math.max(2, colWidth + 1 - scrollX) : 2;
    const pinX = scrollX + pinOffset;

    ctx.font = '9px monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const refLabelW = ctx.measureText('1.2×').width;
    const refLabelH = 11;
    ctx.globalAlpha = 0.75;
    ctx.fillStyle = theme.tooltipBackground;
    ctx.fillRect(pinX - 1, yRatio(1.2) - refLabelH / 2, refLabelW + 3, refLabelH);
    ctx.fillRect(pinX - 1, yRatio(1.0) - refLabelH / 2, refLabelW + 3, refLabelH);
    ctx.globalAlpha = 1.0;
    ctx.fillStyle = theme.mutedForeground;
    ctx.fillText('1.2×', pinX, yRatio(1.2));
    ctx.fillText('1.0×', pinX, yRatio(1.0));

    ctx.font = 'bold 10px monospace';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    const labelText = 'Overload';
    const labelWidth = ctx.measureText(labelText).width;
    ctx.fillStyle = theme.tooltipBackground;
    ctx.fillRect(pinX, 1, labelWidth + 4, 12);
    ctx.fillStyle = theme.mutedForeground;
    ctx.fillText(labelText, pinX + 2, 2);

    if (lv) {
      // "?" help glyph — same constants as TickOverview.
      ctx.font = 'bold 11px monospace';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'alphabetic';
      const glyphRight = width - HELP_GLYPH_MARGIN_RIGHT;
      const bgW = HELP_ICON_GLYPH_WIDTH + 6;
      const bgH = 14;
      ctx.fillStyle = theme.tooltipBackground;
      ctx.fillRect(glyphRight - bgW + 3, HELP_GLYPH_Y_BASELINE - 11, bgW, bgH);
      ctx.fillStyle = helpHoveredRef.current ? theme.foreground : theme.mutedForeground;
      ctx.fillText('?', glyphRight, HELP_GLYPH_Y_BASELINE);

    }
  }, [scrollable]);

  // legendsVisible toggles glyph rendering — not a dep of draw (read via ref) but must trigger redraw.
  useEffect(() => { draw(); }, [draw, legendsVisible]);

  useEffect(() => {
    const ro = new ResizeObserver(() => draw());
    if (containerRef.current) ro.observe(containerRef.current);
    return () => ro.disconnect();
  }, [draw]);

  // Redirect vertical wheel to horizontal scroll when in scrollable mode.
  useEffect(() => {
    if (!scrollable) return;
    const el = containerRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (e.deltaY === 0) return;
      e.preventDefault();
      el.scrollLeft -= e.deltaY * (e.shiftKey ? 5 : 1);
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [scrollable]);

  // Redraw when the container scrolls so the pinned labels follow the viewport.
  useEffect(() => {
    if (!scrollable) return;
    const el = containerRef.current;
    if (!el) return;
    el.addEventListener('scroll', draw);
    return () => el.removeEventListener('scroll', draw);
  }, [scrollable, draw]);

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    const rs = rowsRef.current;
    if (!canvas || rs.length === 0) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const cw = rect.width;
    const lv = legendsRef.current;

    // Help glyph.
    if (isInHelpHitZone(mx, my, cw, lv)) {
      if (tickTooltip !== null) setTickTooltip(null);
      setHelpTooltip({ clientX: e.clientX, clientY: rect.bottom });
      if (!helpHoveredRef.current) { helpHoveredRef.current = true; draw(); }
      setCursor('pointer');
      return;
    }
    if (helpHoveredRef.current) { helpHoveredRef.current = false; setHelpTooltip(null); draw(); }

    setCursor('crosshair');

    // Per-tick tooltip.
    const idx = Math.min(rs.length - 1, Math.max(0, Math.floor((mx / cw) * rs.length)));
    const r = rs[idx];
    const tm = targetMsRef.current;
    const intentLabel = r.intent === 0 ? 'CatchUp' : r.intent === 1 ? 'Throttled' : r.intent === 2 ? 'Headroom' : `?${r.intent}`;
    const lines: string[] = [
      `Tick ${r.tickNumber}`,
      `Duration: ${(r.durationUs / 1000).toFixed(2)} ms`,
      `Ratio: ${r.ratio.toFixed(2)}×  (target ${tm.toFixed(2)} ms)`,
    ];
    if (r.multiplier > 1) lines.push(`Throttled: mult=${r.multiplier} (level ${r.level})`);
    if (r.waitUs > 0) {
      const waitLabel = r.waitUs >= 65535 ? '≥65 ms' : `${(r.waitUs / 1000).toFixed(1)} ms`;
      lines.push(`Pre-tick wait: ${waitLabel} (${intentLabel})`);
    }
    if (r.consecOver > 0) lines.push(`Streak overrun: ${r.consecOver} / 5  (escalate at 5)`);
    else if (r.consecUnder > 0) lines.push(`Streak underrun: ${r.consecUnder} / 20  (deescalate at 20)`);
    setTickTooltip({ lines, clientX: e.clientX, clientY: rect.bottom });
  }, [tickTooltip, draw]);

  const onPointerLeave = useCallback(() => {
    let needRedraw = false;
    if (helpHoveredRef.current) { helpHoveredRef.current = false; needRedraw = true; }
    if (tickTooltip !== null) setTickTooltip(null);
    if (helpTooltip !== null) setHelpTooltip(null);
    setCursor('crosshair');
    if (needRedraw) draw();
  }, [tickTooltip, helpTooltip, draw]);

  const overlay = helpTooltip !== null ? (
    <HelpOverlay lines={OVERLOAD_HELP_LINES} clientX={helpTooltip.clientX} clientY={helpTooltip.clientY} />
  ) : tickTooltip !== null ? (
    <HelpOverlay lines={tickTooltip.lines} clientX={tickTooltip.clientX} clientY={tickTooltip.clientY} gap={2} />
  ) : null;

  const canvasEl = (
    <canvas
      ref={canvasRef}
      className="h-full w-full touch-none select-none"
      style={{ cursor }}
      onPointerMove={onPointerMove}
      onPointerLeave={onPointerLeave}
    />
  );

  // Expand button: HTML overlay so we can use a Lucide icon instead of a canvas-drawn glyph.
  // Positioned to the left of the "?" glyph — mirroring the previous canvas layout.
  const expandBtn = showExpandButton ? (
    <button
      type="button"
      className="absolute flex items-center justify-center rounded text-muted-foreground hover:text-foreground"
      style={{ top: '2px', right: '26px', width: '14px', height: '14px' }}
      onClick={() => onExpandClick?.(containerRef.current!.getBoundingClientRect())}
      title="Expand"
    >
      <Expand className="h-2.5 w-2.5" />
    </button>
  ) : null;

  if (scrollable) {
    return (
      <div ref={containerRef} className="relative h-full w-full overflow-x-auto">
        <div ref={innerDivRef} className="h-full" style={{ minWidth: '100%' }}>
          {canvasEl}
        </div>
        {overlay}
      </div>
    );
  }

  return (
    <div ref={containerRef} className="relative h-full w-full">
      {canvasEl}
      {expandBtn}
      {overlay}
    </div>
  );
}

// ─── OverloadStrip ──────────────────────────────────────────────────────────

export default function OverloadStrip() {
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const baseTickRate = Number(metadata?.header?.baseTickRate ?? 60);
  const targetMsForBaseRate = 1000 / Math.max(baseTickRate, 1);

  const rows = useMemo(() => {
    if (!metadata?.tickSummaries) return [];
    const out = new Array<OverloadRow>(metadata.tickSummaries.length);
    for (let i = 0; i < metadata.tickSummaries.length; i++) {
      const s = metadata.tickSummaries[i];
      const durationUs = Number(s.durationUs);
      const actualMs   = durationUs / 1000;
      const ratio      = targetMsForBaseRate > 0 ? actualMs / targetMsForBaseRate : 0;
      out[i] = {
        tickNumber:   Number(s.tickNumber),
        ratio,
        multiplier:   Number(s.tickMultiplier    ?? 0),
        level:        Number(s.overloadLevel     ?? 0),
        waitUs:       Number(s.metronomeWaitUs   ?? 0),
        intent:       Number(s.metronomeIntentClass ?? 0),
        durationUs,
        consecOver:   Number(s.consecutiveOverrun  ?? 0),
        consecUnder:  Number(s.consecutiveUnderrun ?? 0),
      };
    }
    return out;
  }, [metadata, targetMsForBaseRate]);

  const visible = useMemo(() => {
    for (let i = 0; i < rows.length; i++) {
      if (rows[i].multiplier > 1 || rows[i].ratio >= 1.0) return true;
    }
    return false;
  }, [rows]);

  const [popupRect, setPopupRect] = useState<DOMRect | null>(null);
  const popupRef = useRef<HTMLDivElement>(null);

  // Close popup when the strip hides (clean trace loaded or session reset).
  useEffect(() => { if (!visible) setPopupRect(null); }, [visible]);

  // Close popup on click outside. Uses pointerdown in capture phase so it fires before
  // TimeArea's pointer-capture handler can swallow the event.
  useEffect(() => {
    if (!popupRect) return;
    const onPointerDown = (e: PointerEvent) => {
      if (popupRef.current && !popupRef.current.contains(e.target as Node)) {
        setPopupRect(null);
      }
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => document.removeEventListener('pointerdown', onPointerDown, true);
  }, [popupRect]);

  if (!visible) return null;

  const handleExpand = (rect: DOMRect) => setPopupRect((prev) => (prev !== null ? null : rect));

  const popupTop = popupRect
    ? Math.min(popupRect.bottom, window.innerHeight - POPUP_TOTAL_HEIGHT)
    : 0;

  return (
    <>
      <div className="w-full shrink-0 border-b border-border" style={{ height: `${STRIP_HEIGHT}px` }}>
        <OverloadCanvas
          rows={rows}
          targetMsForBaseRate={targetMsForBaseRate}
          scrollable={false}
          showExpandButton={true}
          onExpandClick={handleExpand}
        />
      </div>

      {popupRect && createPortal(
        <div
          ref={popupRef}
          className="border border-border bg-card shadow-lg"
          style={{
            position: 'fixed',
            top: popupTop,
            left: popupRect.left,
            width: popupRect.width,
            height: POPUP_TOTAL_HEIGHT,
            zIndex: 50,
          }}
        >
          <div
            className="flex items-center justify-between border-b border-border px-2"
            style={{ height: `${POPUP_HEADER_HEIGHT}px` }}
          >
            <span className="select-none text-fs-sm font-semibold text-foreground">Overload diagnostics</span>
            <button
              type="button"
              onClick={() => setPopupRect(null)}
              className="rounded p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </div>
          <div style={{ height: `${POPUP_CANVAS_HEIGHT}px` }}>
            <OverloadCanvas
              rows={rows}
              targetMsForBaseRate={targetMsForBaseRate}
              scrollable={true}
              showExpandButton={false}
              onExpandClick={undefined}
            />
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}

// ─── helpers ──────────────────────────────────────────────────────────────

/**
 * Multiplier → bar tint. Mirrors <c>multiplierBarTint</c> in <c>tickOverview.ts</c> so the two
 * strips read as a single visual story. Returns null for mult&lt;=1 — caller falls back to a
 * ratio-based hue (amber if &gt;=1.0, red if &gt;=1.2, normal otherwise).
 */
function multiplierTint(multiplier: number): string | null {
  if (multiplier <= 1) return null;
  if (multiplier === 2) return '#d97706';
  if (multiplier === 3) return '#ea580c';
  if (multiplier === 4) return '#dc2626';
  return '#991b1b';
}
