import { createPortal } from 'react-dom';
import type { TooltipLine } from '@/libs/profiler/canvas/canvasUtils';

interface Props {
  /** Lines to render (already built by `buildGaugeTooltipLines`). Empty → component renders nothing. */
  lines: readonly TooltipLine[];
  /** Cursor client-space anchor (from the pointer event). Overlay positions near this + edge-collision. */
  clientX: number;
  clientY: number;
}

/** Cursor-to-tooltip gap — applied on whichever side the tooltip lands. */
const GAP = 14;
/** Max tooltip width estimate used ONLY to decide whether the right side has enough room. */
const MAX_W = 320;
/** Per-line height used ONLY to decide whether below-cursor has enough room. */
const LINE_H = 16;

/**
 * DOM overlay for gauge hover tooltips — a multi-line formatted block positioned near the cursor
 * with viewport-edge collision handling. Uses shadcn popover tokens (`bg-popover` /
 * `text-popover-foreground`) so the tooltip flips with the active theme automatically — no
 * per-theme hex.
 *
 * **Rendered via createPortal into document.body** — dockview wraps its panel content in elements
 * with `transform` / `contain` rules that (a) anchor `position: fixed` to themselves rather than
 * the viewport (breaking placement near the cursor) and (b) create stacking contexts where the
 * dock pane separator can paint over our z-index. A portal to body sidesteps both.
 *
 * **Anchoring strategy** — when flipping to the left/top side we anchor the tooltip's RIGHT/BOTTOM
 * edge via CSS `right: ...` / `bottom: ...` rather than computing `left`/`top` from a width
 * *estimate*. The estimate can be way larger than the actual content width, which would push the
 * tooltip far to the left of the cursor when flipping (the "too left" bug). Right-edge anchoring
 * lets the browser size the box from the content and keeps the gap to the cursor exactly `GAP`.
 */
export function GaugeTooltip({ lines, clientX, clientY }: Props): React.JSX.Element | null {
  if (lines.length === 0) return null;
  if (typeof document === 'undefined') return null;

  const viewportW = typeof window !== 'undefined' ? window.innerWidth : 1920;
  const viewportH = typeof window !== 'undefined' ? window.innerHeight : 1080;
  const estH = lines.length * LINE_H + 16;

  const showOnLeft = clientX + GAP + MAX_W > viewportW - 8;
  const showAbove = clientY + GAP + estH > viewportH - 8;

  // Horizontal: anchor left-edge OR right-edge (not both).
  // Vertical: anchor top-edge OR bottom-edge (not both).
  const style: React.CSSProperties = {
    ...(showOnLeft
      ? { right: `${Math.max(8, viewportW - clientX + GAP)}px` }
      : { left: `${Math.min(clientX + GAP, viewportW - 8)}px` }),
    ...(showAbove
      ? { bottom: `${Math.max(8, viewportH - clientY + GAP)}px` }
      : { top: `${Math.min(clientY + GAP, viewportH - 8)}px` }),
    maxWidth: 'calc(100vw - 16px)',
    maxHeight: 'calc(100vh - 16px)',
  };

  return createPortal(
    <div
      className="pointer-events-none fixed z-[9999] overflow-hidden whitespace-pre rounded-[2px] border border-border bg-popover px-3 py-2 font-mono text-fs-sm leading-4 text-popover-foreground shadow-lg"
      style={style}
    >
      {lines.map((line, i) => {
        if (typeof line === 'string') {
          return (
            <div key={i} className="whitespace-pre">
              {line || ' '}
            </div>
          );
        }
        return (
          <div key={i} className="whitespace-pre" style={line.color ? { color: line.color } : undefined}>
            {line.text}
          </div>
        );
      })}
    </div>,
    document.body,
  );
}
