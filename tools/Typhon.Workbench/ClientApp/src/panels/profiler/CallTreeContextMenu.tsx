import { useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { ChevronsDownUp, ChevronsUpDown, Copy, Crosshair, ExternalLink, FileCode } from 'lucide-react';

/**
 * Right-click context menu for a Call Tree row (#351). Anchored at the cursor; opened by {@link CallTree}
 * when a row is right-clicked. Portaled to `document.body` so dockview chrome never paints over it.
 *
 * Items:
 *  - **Show inline** — render the frame's source in the Source Preview panel *and* surface that panel.
 *    This is the deliberate difference from a plain row-select, which only syncs an already-open panel.
 *  - **Open in editor** — open the frame's source `file:line` in the external editor.
 *  - **Focus tree on this frame** — re-root the folded tree at this frame (the drill action).
 *  - **Expand / Collapse subtree** — recursively open / close this frame and every descendant.
 *  - **Copy method name / full signature** — the friendly `Type.Method` name vs the full CLR declaration.
 *
 * "Show inline" / "Open in editor" need a resolved source location — disabled for BCL / native / dynamic
 * frames with no PDB (`sourceAvailable` false). Expand / Collapse are disabled on a leaf (`hasChildren` false).
 */
export interface CallTreeContextMenuProps {
  /** Cursor screen position to anchor the menu at (clientX / clientY). */
  x: number;
  y: number;
  /** Friendly method name of the right-clicked frame — the menu header and "Copy method name". */
  methodName: string;
  /** Full CLR declaration of the frame — "Copy full signature". Falls back to the friendly name. */
  fullSignature: string;
  /** True when the frame resolves to a source `file:line` (engine / user assemblies). */
  sourceAvailable: boolean;
  /** True when the frame has at least one callee — gates Expand / Collapse subtree. */
  hasChildren: boolean;
  onClose: () => void;
  /** Show the frame's source in the Source Preview panel and surface that panel. */
  onShowInline: () => void;
  /** Open the frame's source in the external editor. */
  onOpenInEditor: () => void;
  /** Re-root the folded tree at this frame. */
  onFocusTree: () => void;
  /** Recursively expand this frame and every descendant. */
  onExpandSubtree: () => void;
  /** Recursively collapse this frame and every descendant. */
  onCollapseSubtree: () => void;
}

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

export function CallTreeContextMenu(props: CallTreeContextMenuProps): React.JSX.Element | null {
  const ref = useRef<HTMLDivElement | null>(null);

  // Close on any outside pointer-down, Escape, or a wheel gesture — the tree scrolls on wheel, which
  // would otherwise leave the menu anchored over a row that has scrolled away.
  useEffect(() => {
    // pointerdown, not mousedown — pointerdown always fires (a canvas may preventDefault() it, which
    // suppresses the compatibility mousedown but not pointerdown itself). Consistent across all menus.
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
      className="fixed z-[10000] min-w-56 rounded border border-border bg-popover p-1 text-popover-foreground shadow-md"
      style={{ left: props.x, top: props.y }}
      data-testid="call-tree-context-menu"
    >
      <div className="truncate px-2 py-1 text-[11px] font-semibold text-muted-foreground">{props.methodName}</div>
      <div className="my-1 border-t border-border" />
      <MenuItem
        icon={<FileCode className={icon} />}
        label="Show inline (Source Preview)"
        disabled={!props.sourceAvailable}
        onClick={props.onShowInline}
      />
      <MenuItem
        icon={<ExternalLink className={icon} />}
        label="Open in editor"
        disabled={!props.sourceAvailable}
        onClick={props.onOpenInEditor}
      />
      <div className="my-1 border-t border-border" />
      <MenuItem icon={<Crosshair className={icon} />} label="Focus tree on this frame" onClick={props.onFocusTree} />
      <MenuItem
        icon={<ChevronsUpDown className={icon} />}
        label="Expand subtree"
        disabled={!props.hasChildren}
        onClick={props.onExpandSubtree}
      />
      <MenuItem
        icon={<ChevronsDownUp className={icon} />}
        label="Collapse subtree"
        disabled={!props.hasChildren}
        onClick={props.onCollapseSubtree}
      />
      <div className="my-1 border-t border-border" />
      <MenuItem icon={<Copy className={icon} />} label="Copy method name" onClick={() => copy(props.methodName)} />
      <MenuItem icon={<Copy className={icon} />} label="Copy full signature" onClick={() => copy(props.fullSignature)} />
      {!props.sourceAvailable && (
        <div className="px-2 py-1 text-[10px] leading-tight text-muted-foreground">
          No source — BCL / native frame.
        </div>
      )}
    </div>,
    document.body,
  );
}
