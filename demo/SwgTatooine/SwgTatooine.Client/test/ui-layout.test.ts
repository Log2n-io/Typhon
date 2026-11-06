import { DEFAULT_TABS } from '../src/data/chat-tabs';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  clampOffset,
  clampRect,
  defaultLayout,
  layoutKey,
  loadLayout,
  parseLayout,
  saveLayout,
  TITLE_BAR_PX,
} from '../src/data/ui-layout';
import { useWindows } from '../src/state/windows-store';

/**
 * The interface's layout (client.md C36, U1-1 and U1-2): a window's rect kept on screen, a bar's offset too, the layout kept per account and read
 * back field by field, and the window stack — the front one closed by Escape, a press bringing one to the front.
 */

function memoryStorage(): Storage {
  const items = new Map<string, string>();
  return {
    get length() {
      return items.size;
    },
    clear: () => {
      items.clear();
    },
    getItem: (k) => items.get(k) ?? null,
    key: (i) => [...items.keys()][i] ?? null,
    removeItem: (k) => {
      items.delete(k);
    },
    setItem: (k, v) => {
      items.set(k, v);
    },
  };
}

describe('a window rect', () => {
  it('stays inside the viewport, its title bar reachable', () => {
    expect(clampRect({ x: 900, y: 700, w: 300, h: 200 }, 1000, 800, 240, 120)).toEqual({
      x: 700,
      y: 600,
      w: 300,
      h: 200,
    });
    expect(clampRect({ x: -50, y: -20, w: 300, h: 0 }, 1000, 800, 240, 120)).toEqual({ x: 0, y: 0, w: 300, h: 0 });
    // As tall as its content: only its title bar is kept on screen.
    expect(clampRect({ x: 10, y: 900, w: 300, h: 0 }, 1000, 800, 240, 120)).toEqual({
      x: 10,
      y: 800 - TITLE_BAR_PX,
      w: 300,
      h: 0,
    });
  });

  it('is no smaller than its minimum and no larger than the viewport', () => {
    expect(clampRect({ x: 0, y: 0, w: 50, h: 30 }, 1000, 800, 240, 120)).toEqual({ x: 0, y: 0, w: 240, h: 120 });
    // A layout made on a larger screen: shrunk to this one.
    expect(clampRect({ x: 1500, y: 0, w: 1800, h: 1200 }, 1000, 800, 240, 120)).toEqual({
      x: 0,
      y: 0,
      w: 1000,
      h: 800,
    });
    // A viewport under the minimum: the viewport wins.
    expect(clampRect({ x: 0, y: 0, w: 300, h: 200 }, 200, 100, 240, 120)).toEqual({ x: 0, y: 0, w: 200, h: 100 });
  });
});

describe('a bar offset', () => {
  const base = { left: 10, top: 700, width: 300, height: 80 };

  it('keeps the bar inside the viewport', () => {
    expect(clampOffset({ dx: 50, dy: -100 }, base, 1000, 800)).toEqual({ dx: 50, dy: -100 });
    expect(clampOffset({ dx: -100, dy: 100 }, base, 1000, 800)).toEqual({ dx: -10, dy: 20 });
    expect(clampOffset({ dx: 2000, dy: -2000 }, base, 1000, 800)).toEqual({ dx: 690, dy: -700 });
  });
});

describe('a stored layout', () => {
  it('starts with the bars locked, the debug layer off for a player and on for the god camera', () => {
    expect(defaultLayout(true)).toMatchObject({ locked: true, debug: false, windows: {}, bars: {} });
    expect(defaultLayout(false).debug).toBe(true);
  });

  it('is kept per account and read back', () => {
    const storage = memoryStorage();
    const layout = {
      ...defaultLayout(true),
      windows: { bazaar: { x: 20, y: 70, w: 640, h: 0 } },
      bars: { chat: { dx: 5, dy: -40 } },
      debug: true,
    };
    saveLayout('alice', layout, storage);
    expect(storage.getItem(layoutKey('alice'))).not.toBeNull();
    expect(loadLayout('alice', defaultLayout(true), storage)).toEqual(layout);
    expect(loadLayout('bob', defaultLayout(true), storage)).toEqual(defaultLayout(true));
  });

  it('drops what it cannot read, field by field', () => {
    const fallback = defaultLayout(true);
    expect(parseLayout('not json', fallback)).toBe(fallback);
    expect(parseLayout('42', fallback)).toBe(fallback);
    const read = parseLayout(
      JSON.stringify({
        windows: { a: { x: 1, y: 2, w: 300, h: 0 }, b: { x: 'no' }, c: { x: 1, y: 2, w: 0, h: 0 } },
        bars: { chat: { dx: 3, dy: 4 }, ham: { dx: null } },
        chatW: -5,
        locked: 'yes',
        debug: true,
      }),
      fallback,
    );
    expect(read).toEqual({
      windows: { a: { x: 1, y: 2, w: 300, h: 0 } },
      bars: { chat: { dx: 3, dy: 4 } },
      chatW: 0,
      chatH: 0,
      locked: true,
      debug: true,
      navHidden: [],
      chatTabs: DEFAULT_TABS,
      inventoryPreview: false,
      bazaarPreview: true,
    });
  });

  it('keeps the inventory preview with the rect it widened, and closes it with a reset (client.md C43)', () => {
    const windows = useWindows.getState();
    windows.setInventoryPreview(true, { x: 10, y: 20, w: 602, h: 0 });
    expect(useWindows.getState().layout.inventoryPreview).toBe(true);
    expect(useWindows.getState().layout.windows.inventory).toEqual({ x: 10, y: 20, w: 602, h: 0 });
    expect(parseLayout(JSON.stringify(useWindows.getState().layout), defaultLayout(true)).inventoryPreview).toBe(true);
    useWindows.getState().resetLayout();
    expect(useWindows.getState().layout.inventoryPreview).toBe(false);
    expect(useWindows.getState().layout.windows.inventory).toBeUndefined();
  });

  it("opens the bazaar's preview by default, and a reset opens it again (Loïc, 2026-11-05)", () => {
    const windows = useWindows.getState();
    windows.setBazaarPreview(false, { x: 20, y: 70, w: 662, h: 0 });
    expect(useWindows.getState().layout.bazaarPreview).toBe(false);
    expect(useWindows.getState().layout.windows.bazaar).toEqual({ x: 20, y: 70, w: 662, h: 0 });
    useWindows.getState().resetLayout();
    expect(useWindows.getState().layout.bazaarPreview).toBe(true);
    expect(useWindows.getState().layout.windows.bazaar).toBeUndefined();
  });

  it('is not kept when storage refuses, and reads as the fallback', () => {
    const refusing = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    };
    expect(() => {
      saveLayout('alice', defaultLayout(true), refusing);
    }).not.toThrow();
    expect(loadLayout('alice', defaultLayout(true), refusing)).toEqual(defaultLayout(true));
  });
});

describe('the window stack', () => {
  beforeEach(() => {
    for (const id of useWindows.getState().stack) {
      useWindows.getState().unregister(id);
    }
  });

  it('puts a window opened on top, a pressed one to the front, and Escape closes the front one only', () => {
    const closed: string[] = [];
    const windows = useWindows.getState;
    windows().register('inventory', () => {
      closed.push('inventory');
      windows().unregister('inventory');
    });
    windows().register('bazaar', () => {
      closed.push('bazaar');
      windows().unregister('bazaar');
    });
    expect(windows().stack).toEqual(['inventory', 'bazaar']);
    windows().focus('inventory');
    expect(windows().stack).toEqual(['bazaar', 'inventory']);

    expect(windows().closeTop()).toBe(true);
    expect(closed).toEqual(['inventory']);
    expect(windows().stack).toEqual(['bazaar']);
    expect(windows().closeTop()).toBe(true);
    expect(windows().closeTop()).toBe(false);
    expect(closed).toEqual(['inventory', 'bazaar']);
  });

  it('keeps a window placed, and a reset forgets every place but keeps the lock and the debug layer', () => {
    const windows = useWindows.getState;
    windows().load('layout-test', true);
    windows().place('map', { x: 100, y: 50, w: 500, h: 560 });
    windows().moveBar('chat', { dx: 10, dy: -20 });
    windows().setDebug(true);
    windows().setLocked(false);
    expect(windows().layout.windows.map).toEqual({ x: 100, y: 50, w: 500, h: 560 });

    windows().resetLayout();
    expect(windows().layout).toMatchObject({ windows: {}, bars: {}, debug: true, locked: false });
    windows().toggleDebug();
    expect(windows().layout.debug).toBe(false);
  });
});
