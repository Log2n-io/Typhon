import { create } from 'zustand';
import { parseCraft, parseSchematics, type CraftUnderWay, type SchematicBook } from '../data/craft';
import type { MarketCommand } from '../data/market';
import { useMarket } from './market-store';

/**
 * The crafting panel's state (crafting-build.md C2): the schematics, read once; the craft under way, read again whenever the body's `items` revision
 * moves (every craft step moves it); and the inputs picked for the next start. Commands go out through the bazaar's actions, so their refusals land on
 * its `'craft'` row like any other.
 */
export interface CraftStore {
  readonly open: boolean;
  readonly book: SchematicBook | null;
  readonly craft: CraftUnderWay | null;
  /** The schematic picked to start, by id. */
  readonly selected: number | null;
  /** Per material or core slot in order, the Mk picked; per part, in order, the instance id picked. */
  readonly mks: readonly number[];
  readonly parts: readonly string[];
  readonly toggle: () => void;
  readonly close: () => void;
  readonly select: (id: number | null) => void;
  readonly setMk: (index: number, mk: number) => void;
  readonly setPart: (index: number, id: string) => void;
  readonly setChoice: (mks: readonly number[], parts: readonly string[]) => void;
  readonly refresh: () => Promise<void>;
  readonly start: () => void;
  readonly point: (line: number) => void;
  readonly enhance: (mk: number) => void;
  readonly finish: () => void;
}

const fetchJson = async (url: string): Promise<unknown> => {
  const actions = useMarket.getState().actions;
  if (actions === null) {
    return null;
  }

  const token = actions.token();
  try {
    const response = await actions.fetch(url, {
      headers: token === null ? {} : { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(10_000),
    });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
};

const send = (command: MarketCommand): void => {
  useMarket.getState().actions?.send(command);
};

export const useCraft = create<CraftStore>((set, get) => ({
  open: false,
  book: null,
  craft: null,
  selected: null,
  mks: [],
  parts: [],
  toggle: () => {
    const open = !get().open;
    set({ open });
    if (open) {
      void get().refresh();
    }
  },
  close: () => {
    set({ open: false });
  },
  select: (id) => {
    set({ selected: id, mks: [], parts: [] });
  },
  setMk: (index, mk) => {
    const mks = [...get().mks];
    mks[index] = mk;
    set({ mks });
  },
  setPart: (index, id) => {
    const parts = [...get().parts];
    parts[index] = id;
    set({ parts });
  },
  setChoice: (mks, parts) => {
    set({ mks, parts });
  },
  refresh: async () => {
    if (get().book === null) {
      const book = parseSchematics(await fetchJson('swg/schematics'));
      if (book !== null) {
        set({ book });
      }
    }

    set({ craft: parseCraft(await fetchJson('swg/craft')) });
  },
  start: () => {
    const { selected, mks, parts } = get();
    if (selected !== null) {
      send({ kind: 'craftStart', schematic: selected, mks, parts });
    }
  },
  point: (line) => {
    send({ kind: 'craftPoint', line });
  },
  enhance: (mk) => {
    send({ kind: 'craftEnhance', mk });
  },
  finish: () => {
    const craft = get().craft;
    if (craft !== null) {
      send({ kind: 'craftFinish', schematic: craft.schematic });
    }
  },
}));
