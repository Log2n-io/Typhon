import { DEFAULT_TABS, parseTabs, type ChatTab } from './chat-tabs';
/**
 * The interface's layout (client.md C36): where each game window stands and how big it is, how far each bar was moved from where it is drawn,
 * whether the bars are locked, and whether the debug layer shows.
 *
 * Kept in `localStorage` per account, like the HUD's folds (`data/hud-sections`): a display preference of this browser. Storage can be missing or
 * refuse; then nothing is kept and every window opens where it opens by default.
 */

/** A game window's place and size, CSS pixels from the viewport's top left. A height of 0 is no height: the window is as tall as its content. */
export interface WindowRect {
  readonly x: number;
  readonly y: number;
  readonly w: number;
  readonly h: number;
}

/** How far a bar was moved from where its stylesheet draws it, CSS pixels. */
export interface BarOffset {
  readonly dx: number;
  readonly dy: number;
}

export interface UiLayout {
  readonly windows: Readonly<Partial<Record<string, WindowRect>>>;
  readonly bars: Readonly<Partial<Record<string, BarOffset>>>;
  /** The chat's size, CSS pixels; 0 for its stylesheet's. */
  readonly chatW: number;
  readonly chatH: number;
  /** Whether the bars stay where they are: true unless the player unlocked them. */
  readonly locked: boolean;
  /** Whether the debug layer shows: the world toolbar, the HUD, the waypoint pane's scan line. */
  readonly debug: boolean;
  /** What the waypoint pane leaves out (client.md C38): `city:<StructureKind>`, `mission`, `structure` (the player's pins), `custom`. */
  readonly navHidden: readonly string[];
  /** The chat's tabs (client.md C44): a name and the categories each shows. */
  readonly chatTabs: readonly ChatTab[];
  /**
   * Whether the inventory's preview panel is open (client.md C43). Kept with the window's rect it widened: kept apart, a reload showed the wide rect
   * without the panel, and the next toggle widened it again.
   */
  readonly inventoryPreview: boolean;
}

/** The layout of a first visit: nothing moved, the bars locked, the debug layer shown only to a session with no player (the god camera). */
export function defaultLayout(playing: boolean): UiLayout {
  return {
    windows: {},
    bars: {},
    chatW: 0,
    chatH: 0,
    locked: true,
    debug: !playing,
    navHidden: [],
    chatTabs: DEFAULT_TABS,
    inventoryPreview: false,
  };
}

/** How much of a window must stay on screen: its title bar, so it can always be dragged back. */
export const TITLE_BAR_PX = 32;

/** A window's rect kept on screen: no wider or taller than the viewport, no smaller than its minimum, its title bar inside the viewport. */
export function clampRect(r: WindowRect, vw: number, vh: number, minW: number, minH: number): WindowRect {
  const w = Math.max(Math.min(minW, vw), Math.min(r.w, vw));
  const h = r.h === 0 ? 0 : Math.max(Math.min(minH, vh), Math.min(r.h, vh));
  const x = Math.min(Math.max(0, r.x), Math.max(0, vw - w));
  const y = Math.min(Math.max(0, r.y), Math.max(0, vh - (h === 0 ? TITLE_BAR_PX : h)));
  return { x: Math.round(x), y: Math.round(y), w: Math.round(w), h: Math.round(h) };
}

/** A bar's offset kept on screen, from where its box stands at no offset (`base`, a DOMRect's fields) in a viewport of vw × vh. */
export function clampOffset(
  o: BarOffset,
  base: { readonly left: number; readonly top: number; readonly width: number; readonly height: number },
  vw: number,
  vh: number,
): BarOffset {
  const dx = Math.min(Math.max(o.dx, -base.left), Math.max(-base.left, vw - base.left - base.width));
  const dy = Math.min(Math.max(o.dy, -base.top), Math.max(-base.top, vh - base.top - base.height));
  return { dx: Math.round(dx), dy: Math.round(dy) };
}

/** The storage key of an account's layout. */
export function layoutKey(account: string | null): string {
  return `swg.layout.${account ?? 'anonymous'}`;
}

const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

/** The layout a stored value holds; `fallback` for anything unreadable, and each field that is not what it should be dropped on its own. */
export function parseLayout(raw: string | null, fallback: UiLayout): UiLayout {
  if (raw === null) {
    return fallback;
  }

  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return fallback;
  }

  if (typeof value !== 'object' || value === null) {
    return fallback;
  }

  const v = value as Record<string, unknown>;
  const windows: Record<string, WindowRect> = {};
  if (typeof v.windows === 'object' && v.windows !== null) {
    for (const [id, r] of Object.entries(v.windows as Record<string, unknown>)) {
      const rect = r as Record<string, unknown> | null;
      if (
        rect !== null &&
        finite(rect.x) &&
        finite(rect.y) &&
        finite(rect.w) &&
        finite(rect.h) &&
        rect.w > 0 &&
        rect.h >= 0
      ) {
        windows[id] = { x: rect.x, y: rect.y, w: rect.w, h: rect.h };
      }
    }
  }

  const bars: Record<string, BarOffset> = {};
  if (typeof v.bars === 'object' && v.bars !== null) {
    for (const [id, o] of Object.entries(v.bars as Record<string, unknown>)) {
      const offset = o as Record<string, unknown> | null;
      if (offset !== null && finite(offset.dx) && finite(offset.dy)) {
        bars[id] = { dx: offset.dx, dy: offset.dy };
      }
    }
  }

  return {
    windows,
    bars,
    chatW: finite(v.chatW) && v.chatW > 0 ? v.chatW : 0,
    chatH: finite(v.chatH) && v.chatH > 0 ? v.chatH : 0,
    locked: typeof v.locked === 'boolean' ? v.locked : fallback.locked,
    debug: typeof v.debug === 'boolean' ? v.debug : fallback.debug,
    navHidden: Array.isArray(v.navHidden)
      ? (v.navHidden as unknown[]).filter((k): k is string => typeof k === 'string')
      : fallback.navHidden,
    chatTabs: v.chatTabs === undefined ? fallback.chatTabs : parseTabs(v.chatTabs),
    inventoryPreview: typeof v.inventoryPreview === 'boolean' ? v.inventoryPreview : fallback.inventoryPreview,
  };
}

/** An account's stored layout; `fallback` when there is none or storage cannot be read. */
export function loadLayout(
  account: string | null,
  fallback: UiLayout,
  storage: Pick<Storage, 'getItem'> | null = localStorageOrNull(),
): UiLayout {
  try {
    return parseLayout(storage?.getItem(layoutKey(account)) ?? null, fallback);
  } catch {
    return fallback;
  }
}

/** Keeps an account's layout for its next session; nothing when storage refuses. */
export function saveLayout(
  account: string | null,
  layout: UiLayout,
  storage: Pick<Storage, 'setItem'> | null = localStorageOrNull(),
): void {
  try {
    storage?.setItem(layoutKey(account), JSON.stringify(layout));
  } catch {
    // Not kept: the windows open where they open by default next time.
  }
}

// Reading `localStorage` itself throws where site data is blocked.
function localStorageOrNull(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}
