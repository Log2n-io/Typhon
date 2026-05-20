import { useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { Activity, Copy, ExternalLink, FileCode, ZoomIn } from 'lucide-react';

/**
 * Right-click context menu for the Profiler time area. Anchored at the cursor; opened by {@link TimeArea}
 * when a right-click hit-tests onto a span bar or a scheduler-chunk bar. Portaled to `document.body` so
 * dockview chrome never paints over it.
 *
 * The menu has two shapes, discriminated by `kind`:
 *
 *  - **`span`** — a nested instrumentation span. View in Call Tree (every instance of the span's *kind*,
 *    #351 span-kind scope), zoom, open the emission site, copy name / id.
 *  - **`chunk`** — a scheduler chunk, i.e. one system invocation. View in Call Tree (the chunk's
 *    *system*, #351 system scope), zoom, show / open the system source, copy the system name.
 *
 * A single span instance / single chunk is deliberately *not* a Call Tree scope — CPU sampling is 1 kHz,
 * so one slice contains ~0 samples; only the kind / system aggregate is statistically meaningful.
 *
 * Call Tree actions need a trace session (`callTreeAvailable`); source actions need a resolved
 * `file:line` (`sourceAvailable`) — both render disabled otherwise.
 */
interface MenuChrome {
  /** Cursor screen position to anchor the menu at (clientX / clientY). */
  x: number;
  y: number;
  onClose: () => void;
  /** True only for `trace` sessions — the Call Tree (CPU sampling) is file-mode only. */
  callTreeAvailable: boolean;
  /** True when the target resolves to a source `file:line`. */
  sourceAvailable: boolean;
  /** Scope the Call Tree (to the span's kind / the chunk's system) and surface the panel. */
  onViewInCallTree: () => void;
  /** Animate the viewport to the target's bounds. */
  onZoom: () => void;
  /** Open the target's source in the external editor. */
  onOpenSource: () => void;
}

/** Right-click target: an instrumentation span bar. */
export interface SpanMenuProps extends MenuChrome {
  kind: 'span';
  /** Span name — header and "Copy span name". */
  spanName: string;
  /** Span id — "Copy span id"; undefined when the span carries none. */
  spanId?: string;
}

/** Right-click target: a scheduler chunk bar (one system invocation). */
export interface ChunkMenuProps extends MenuChrome {
  kind: 'chunk';
  /** System name — header and "Copy system name". */
  systemName: string;
  /** Show the system source in the Source Preview panel and surface that panel. */
  onShowSourceInline: () => void;
}

export type TimeAreaContextMenuProps = SpanMenuProps | ChunkMenuProps;

/** One clickable row — icon + label, disabled-dim when unavailable. */
function MenuItem(props: {
  icon: React.JSX.Element;
  label: string;
  disabled?: boolean;
  onClick: () => void;
}): React.JSX.Element {
  return (
    <button
      type="button"
      disabled={props.disabled}
      onClick={props.onClick}
      className="flex w-full items-center gap-2 rounded px-2 py-1 text-left text-[11px] text-foreground hover:bg-muted/60 disabled:opacity-40 disabled:hover:bg-transparent"
    >
      <span className="shrink-0 text-muted-foreground">{props.icon}</span>
      <span className="flex-1">{props.label}</span>
    </button>
  );
}

export function TimeAreaContextMenu(props: TimeAreaContextMenuProps): React.JSX.Element | null {
  const ref = useRef<HTMLDivElement | null>(null);

  // Close on any outside pointer-down, Escape, or a wheel gesture — the canvas pans / zooms on wheel,
  // which would otherwise leave the menu anchored over a bar that has scrolled away.
  useEffect(() => {
    // pointerdown, not mousedown: the time-area canvas calls preventDefault() on pointerdown, which
    // suppresses the compatibility mousedown event — a mousedown listener would never see the click.
    const onDown = (e: PointerEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        props.onClose();
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        props.onClose();
      }
    };
    window.addEventListener('pointerdown', onDown);
    window.addEventListener('keydown', onKey);
    window.addEventListener('wheel', props.onClose, { passive: true });
    return () => {
      window.removeEventListener('pointerdown', onDown);
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('wheel', props.onClose);
    };
  }, [props]);

  const copy = (text: string): void => {
    void navigator.clipboard?.writeText(text);
    props.onClose();
  };

  const icon = 'h-3 w-3';
  if (typeof document === 'undefined') {
    return null;
  }
  // Portaled to document.body — dockview's transformed panel ancestors would otherwise re-anchor a
  // `position: fixed` menu to the wrong box, and dockview's resize sashes (z-index up to 9999) paint
  // over a menu confined to the panel's nested stacking context. `z-[10000]` clears all dockview chrome.
  return createPortal(
    <div
      ref={ref}
      className="fixed z-[10000] min-w-52 rounded border border-border bg-popover p-1 text-popover-foreground shadow-md"
      style={{ left: props.x, top: props.y }}
      data-testid="time-area-context-menu"
    >
      <div className="truncate px-2 py-1 text-[11px] font-semibold text-muted-foreground">
        {props.kind === 'span' ? props.spanName : props.systemName}
      </div>
      <div className="my-1 border-t border-border" />
      {props.kind === 'span' ? (
        <>
          <MenuItem
            icon={<Activity className={icon} />}
            label="View in Call Tree (merged)"
            disabled={!props.callTreeAvailable}
            onClick={props.onViewInCallTree}
          />
          <MenuItem icon={<ZoomIn className={icon} />} label="Zoom to span" onClick={props.onZoom} />
          <MenuItem
            icon={<FileCode className={icon} />}
            label="Open emission site in editor"
            disabled={!props.sourceAvailable}
            onClick={props.onOpenSource}
          />
          <div className="my-1 border-t border-border" />
          <MenuItem icon={<Copy className={icon} />} label="Copy span name" onClick={() => copy(props.spanName)} />
          <MenuItem
            icon={<Copy className={icon} />}
            label="Copy span id"
            disabled={props.spanId == null}
            onClick={() => props.spanId != null && copy(props.spanId)}
          />
        </>
      ) : (
        <>
          <MenuItem
            icon={<Activity className={icon} />}
            label="View in Call Tree (system)"
            disabled={!props.callTreeAvailable}
            onClick={props.onViewInCallTree}
          />
          <MenuItem icon={<ZoomIn className={icon} />} label="Zoom to chunk" onClick={props.onZoom} />
          <MenuItem
            icon={<FileCode className={icon} />}
            label="Show system source inline"
            disabled={!props.sourceAvailable}
            onClick={props.onShowSourceInline}
          />
          <MenuItem
            icon={<ExternalLink className={icon} />}
            label="Open system source in editor"
            disabled={!props.sourceAvailable}
            onClick={props.onOpenSource}
          />
          <div className="my-1 border-t border-border" />
          <MenuItem icon={<Copy className={icon} />} label="Copy system name" onClick={() => copy(props.systemName)} />
        </>
      )}
      {!props.callTreeAvailable && (
        <div className="px-2 py-1 text-[10px] leading-tight text-muted-foreground">
          Call Tree is available for trace sessions only.
        </div>
      )}
    </div>,
    document.body,
  );
}
