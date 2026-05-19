import { useEffect, useRef } from 'react';

// The map's right-click context menu (Module 15, A4, §4.6). Copies the byte offset / page index / segment id
// of the clicked cell, and offers the cross-link reveal actions (§7.3) when the panel supplies their handlers.

export interface DbMapContextMenuProps {
  /** Screen position to anchor the menu at. */
  x: number;
  y: number;
  pageIndex: number;
  byteOffset: number;
  /** Owning segment id, or -1 when the cell belongs to no segment. */
  segmentId: number;
  onClose: () => void;
  /** Reveals the cell's segment in the Resource Explorer — omitted when unavailable. */
  onReveal?: () => void;
  /** Opens the cell's component type in the Schema Inspector — omitted when the cell is not a component. */
  onOpenInSchema?: () => void;
}

export function DbMapContextMenu(props: DbMapContextMenuProps) {
  const ref = useRef<HTMLDivElement | null>(null);

  // Close on any outside pointer-down or Escape — the standard transient-menu dismissal.
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        props.onClose();
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        props.onClose();
      }
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [props]);

  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text);
    props.onClose();
  };

  const item = (label: string, onClick: (() => void) | undefined) => (
    <button
      type="button"
      disabled={!onClick}
      onClick={onClick}
      className="w-full rounded px-2 py-1 text-left text-[11px] text-foreground hover:bg-muted/60 disabled:opacity-40 disabled:hover:bg-transparent"
    >
      {label}
    </button>
  );

  return (
    <div
      ref={ref}
      className="fixed z-50 min-w-44 rounded border border-border bg-popover p-1 text-popover-foreground shadow-md"
      style={{ left: props.x, top: props.y }}
      data-testid="dbmap-context-menu"
    >
      {item(`Copy page index (#${props.pageIndex})`, () => copy(String(props.pageIndex)))}
      {item(`Copy byte offset (0x${props.byteOffset.toString(16).toUpperCase()})`, () =>
        copy(`0x${props.byteOffset.toString(16).toUpperCase()}`),
      )}
      {item(
        props.segmentId >= 0 ? `Copy segment id (#${props.segmentId})` : 'Copy segment id — no segment',
        props.segmentId >= 0 ? () => copy(String(props.segmentId)) : undefined,
      )}
      <div className="my-1 border-t border-border" />
      {item('Reveal in Resource Explorer', props.onReveal && (() => {
        props.onReveal?.();
        props.onClose();
      }))}
      {item('Open in Schema Inspector', props.onOpenInSchema && (() => {
        props.onOpenInSchema?.();
        props.onClose();
      }))}
    </div>
  );
}
