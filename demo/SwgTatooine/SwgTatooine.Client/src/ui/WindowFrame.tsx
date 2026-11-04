import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import { clampOffset, clampRect, type BarOffset, type WindowRect } from '../data/ui-layout';
import { useWindows } from '../state/windows-store';

/** The viewport's size, CSS pixels, following its resizes. */
export function useViewport(): { readonly vw: number; readonly vh: number } {
  const [size, setSize] = useState(() => ({ vw: window.innerWidth, vh: window.innerHeight }));
  useEffect(() => {
    const onResize = (): void => {
      setSize({ vw: window.innerWidth, vh: window.innerHeight });
    };
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('resize', onResize);
    };
  }, []);
  return size;
}

/** The z-index of the window at `index` in the stack, back to front: above the bars and prompts, under the context menu and the kick notice. */
export function windowZ(index: number): number {
  return 10 + Math.min(Math.max(index, 0), 7);
}

interface WindowFrameProps {
  /** The window's id in the layout. */
  readonly id: string;
  readonly title: ReactNode;
  /** What the title bar shows right of the title, before the close button: a count, the credits. */
  readonly extra?: ReactNode;
  readonly onClose: () => void;
  /** Where it opens on a first visit, for a viewport of vw × vh. A height of 0: as tall as its content, up to the viewport. */
  readonly initial: (vw: number, vh: number) => WindowRect;
  readonly minW?: number;
  readonly minH?: number;
  readonly className?: string;
  readonly ariaLabel: string;
  readonly children: ReactNode;
}

/**
 * A game window (client.md C36): dragged by its title bar, resized by its corner down to its minimum, closed by ✕ or by Escape when it is the
 * front one, brought to the front by a press anywhere on it. Its rect is kept per account and clamped into the viewport, so a layout made on a
 * larger screen still opens with every title bar reachable.
 */
export function WindowFrame(props: WindowFrameProps) {
  const { id, onClose } = props;
  const stored = useWindows((s) => s.layout.windows[id]);
  const index = useWindows((s) => s.stack.indexOf(id));
  const { vw, vh } = useViewport();
  const minW = props.minW ?? 240;
  const minH = props.minH ?? 120;
  // A drag or a resize under way: drawn from here, kept when the pointer is let go.
  const [live, setLive] = useState<WindowRect | null>(null);
  const start = useRef<{
    readonly px: number;
    readonly py: number;
    readonly rect: WindowRect;
    readonly resize: boolean;
  } | null>(null);
  const root = useRef<HTMLDivElement>(null);

  // Registered while mounted, with the latest close: the store's Escape calls it.
  const closeRef = useRef(onClose);
  useEffect(() => {
    closeRef.current = onClose;
  }, [onClose]);
  useEffect(() => {
    const windows = useWindows.getState();
    windows.register(id, () => {
      closeRef.current();
    });
    return () => {
      useWindows.getState().unregister(id);
    };
  }, [id]);

  const rect = clampRect(live ?? stored ?? props.initial(vw, vh), vw, vh, minW, minH);

  const begin = (e: ReactPointerEvent<HTMLElement>, resize: boolean): void => {
    if (e.button !== 0 || (!resize && (e.target as HTMLElement).closest('button, input, select, a') !== null)) {
      return;
    }

    // A resize from a window as tall as its content starts from the height it is drawn at.
    const drawnH = root.current?.getBoundingClientRect().height ?? rect.h;
    start.current = {
      px: e.clientX,
      py: e.clientY,
      rect: { ...rect, h: resize && rect.h === 0 ? Math.round(drawnH) : rect.h },
      resize,
    };
    e.currentTarget.setPointerCapture(e.pointerId);
    e.preventDefault();
  };

  const move = (e: ReactPointerEvent<HTMLElement>): void => {
    const s = start.current;
    if (s === null) {
      return;
    }

    const dx = e.clientX - s.px;
    const dy = e.clientY - s.py;
    setLive(
      s.resize ? { ...s.rect, w: s.rect.w + dx, h: s.rect.h + dy } : { ...s.rect, x: s.rect.x + dx, y: s.rect.y + dy },
    );
  };

  const end = (): void => {
    if (start.current !== null && live !== null) {
      useWindows.getState().place(id, clampRect(live, vw, vh, minW, minH));
    }

    start.current = null;
    setLive(null);
  };

  const style: CSSProperties = {
    left: rect.x,
    top: rect.y,
    width: rect.w,
    height: rect.h === 0 ? undefined : rect.h,
    maxHeight: rect.h === 0 ? vh - rect.y - 10 : undefined,
    zIndex: windowZ(index),
  };

  return (
    <div
      ref={root}
      className={`panel window${props.className === undefined ? '' : ` ${props.className}`}`}
      role="dialog"
      aria-label={props.ariaLabel}
      style={style}
      data-window={id}
      onPointerDownCapture={() => {
        useWindows.getState().focus(id);
      }}
    >
      <div
        className="missions-head window-title"
        onPointerDown={(e) => {
          begin(e, false);
        }}
        onPointerMove={move}
        onPointerUp={end}
        onPointerCancel={end}
      >
        <span className="hud-title">{props.title}</span>
        {props.extra}
        <button type="button" className="missions-close" onClick={onClose} aria-label="Close" title="Close (Escape)">
          ✕
        </button>
      </div>
      <div className="window-body">{props.children}</div>
      <div
        className="window-grip"
        aria-hidden="true"
        onPointerDown={(e) => {
          begin(e, true);
        }}
        onPointerMove={move}
        onPointerUp={end}
        onPointerCancel={end}
      />
    </div>
  );
}

/**
 * A bar's offset (client.md C36), and the handle that moves it while the interface is unlocked: `style` goes on the bar's own element, `grip`
 * inside it. The offset is a CSS `translate`, which composes with whatever `transform` the bar's stylesheet already uses to place it.
 */
export function useBar(id: string): { readonly style: CSSProperties; readonly grip: ReactNode } {
  const offset = useWindows((s) => s.layout.bars[id]);
  const locked = useWindows((s) => s.layout.locked);
  const [live, setLive] = useState<BarOffset | null>(null);
  const start = useRef<{
    readonly px: number;
    readonly py: number;
    readonly from: BarOffset;
    readonly base: { readonly left: number; readonly top: number; readonly width: number; readonly height: number };
  } | null>(null);
  const shown = live ?? offset ?? { dx: 0, dy: 0 };

  const grip = locked ? null : (
    <div
      className="bar-grip"
      title="Drag to move (lock the interface in Options to stop)"
      onPointerDown={(e) => {
        if (e.button !== 0) {
          return;
        }

        const bar = (e.currentTarget.parentElement ?? e.currentTarget).getBoundingClientRect();
        // The box where it stands with no offset: the offset drawn now taken back off.
        start.current = {
          px: e.clientX,
          py: e.clientY,
          from: shown,
          base: { left: bar.left - shown.dx, top: bar.top - shown.dy, width: bar.width, height: bar.height },
        };
        e.currentTarget.setPointerCapture(e.pointerId);
        e.preventDefault();
        e.stopPropagation();
      }}
      onPointerMove={(e) => {
        const s = start.current;
        if (s !== null) {
          setLive(
            clampOffset(
              { dx: s.from.dx + e.clientX - s.px, dy: s.from.dy + e.clientY - s.py },
              s.base,
              window.innerWidth,
              window.innerHeight,
            ),
          );
        }
      }}
      onPointerUp={() => {
        if (start.current !== null && live !== null) {
          useWindows.getState().moveBar(id, live);
        }

        start.current = null;
        setLive(null);
      }}
    >
      ⠿
    </div>
  );

  return {
    style: shown.dx === 0 && shown.dy === 0 ? {} : { translate: `${String(shown.dx)}px ${String(shown.dy)}px` },
    grip,
  };
}
