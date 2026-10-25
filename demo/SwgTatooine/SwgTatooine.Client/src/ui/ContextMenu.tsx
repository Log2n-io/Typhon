import { useEffect, useRef } from 'react';
import { useUi } from '../state/ui-store';

/** The menu's width, and how close to the window's edges it may open, CSS pixels: it opens at the pointer, inside the window. */
const MENU_WIDTH_PX = 220;
const EDGE_PX = 8;

/**
 * The context menu: what the thing held on offers, at the pointer (`ContextMenuState`). An entry runs its action; Escape,
 * Cancel or a press anywhere else closes it.
 */
export function ContextMenu() {
  const menu = useUi((s) => s.contextMenu);
  const close = useUi((s) => s.closeContextMenu);
  const run = useUi((s) => s.runContextAction);
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (menu === null) {
      return undefined;
    }

    const onKey = (e: KeyboardEvent): void => {
      if (e.code === 'Escape') {
        close();
      }
    };
    // Capture: a press on the canvas is the camera's, which takes the pointer, and the menu must still hear it to close.
    const onDown = (e: PointerEvent): void => {
      if (root.current !== null && !root.current.contains(e.target as Node)) {
        close();
      }
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('pointerdown', onDown, true);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('pointerdown', onDown, true);
    };
  }, [menu, close]);

  if (menu === null) {
    return null;
  }

  // Below the pointer in the window's upper half, above it in the lower: a menu opened low ran under the action bar and
  // off the window, and its height is not known until it is drawn.
  const left = Math.max(EDGE_PX, Math.min(menu.x, window.innerWidth - MENU_WIDTH_PX - EDGE_PX));
  const vertical =
    menu.y <= window.innerHeight / 2
      ? { top: Math.max(EDGE_PX, menu.y) }
      : { bottom: Math.max(EDGE_PX, window.innerHeight - menu.y) };
  return (
    <div
      ref={root}
      className="panel context-menu"
      role="menu"
      aria-label={menu.title}
      style={{ left, ...vertical, width: MENU_WIDTH_PX }}
    >
      <div className="hud-title">{menu.title}</div>
      <div className="context-items">
        {menu.items.map((item) => (
          <button
            key={item.label}
            type="button"
            role="menuitem"
            title={item.hint}
            onClick={() => {
              run(item.action);
            }}
          >
            {item.label}
          </button>
        ))}
      </div>
      <button type="button" className="context-cancel" onClick={close}>
        Cancel
      </button>
    </div>
  );
}
