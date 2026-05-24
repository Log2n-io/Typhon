import { createPortal } from 'react-dom';

interface Props {
  /** Lines of help text. Empty → component renders nothing. */
  lines: readonly string[];
  /** Cursor client-space anchor (from the pointer event). */
  clientX: number;
  clientY: number;
  /** Gap between the anchor point and the tooltip edge (default 14). Smaller values for
   *  non-cursor anchors like "below the overview strip" where 14 looks too disconnected. */
  gap?: number;
}

/** Default cursor-to-tooltip gap — applied on whichever side the tooltip lands. */
const DEFAULT_GAP = 14;
/** Max tooltip width estimate used ONLY to decide which side has enough room. */
const MAX_W = 460;
const LINE_H = 16;

/**
 * Portaled DOM overlay for the "?" help tooltip. Shares theme-aware chrome with
 * {@link GaugeTooltip}; rendered into `document.body` so dockview's transformed ancestors don't
 * anchor our `position: fixed` to the wrong box and so the dock pane separator never paints on
 * top of the tooltip.
 *
 * Anchors the RIGHT/BOTTOM edge via CSS when flipping so the placement stays correct regardless
 * of actual (content-derived) tooltip width — see the comment on {@link GaugeTooltip} for the
 * "too-left bug" this avoids.
 */
export function HelpOverlay({ lines, clientX, clientY, gap = DEFAULT_GAP }: Props): React.JSX.Element | null {
  if (lines.length === 0) return null;
  if (typeof document === 'undefined') return null;

  const viewportW = typeof window !== 'undefined' ? window.innerWidth : 1920;
  const viewportH = typeof window !== 'undefined' ? window.innerHeight : 1080;
  const estH = lines.length * LINE_H + 20;

  const showOnLeft = clientX + gap + MAX_W > viewportW - 8;
  const showAbove = clientY + gap + estH > viewportH - 8;

  const style: React.CSSProperties = {
    ...(showOnLeft
      ? { right: `${Math.max(8, viewportW - clientX + gap)}px` }
      : { left: `${Math.min(clientX + gap, viewportW - 8)}px` }),
    ...(showAbove
      ? { bottom: `${Math.max(8, viewportH - clientY + gap)}px` }
      : { top: `${Math.min(clientY + gap, viewportH - 8)}px` }),
    maxWidth: 'calc(100vw - 16px)',
    maxHeight: 'calc(100vh - 16px)',
  };

  return createPortal(
    <div
      className="pointer-events-none fixed z-[9999] overflow-hidden whitespace-pre rounded-[2px] border border-border bg-popover px-3 py-2 font-mono text-fs-sm leading-4 text-popover-foreground shadow-lg"
      style={style}
    >
      {lines.join('\n')}
    </div>,
    document.body,
  );
}
