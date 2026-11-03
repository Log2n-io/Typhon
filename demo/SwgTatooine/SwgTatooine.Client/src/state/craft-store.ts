import { create } from 'zustand';
import { parseCraft, parseSchematics, type CraftUnderWay, type SchematicBook } from '../data/craft';
import { mkNumeral, type InventoryItem } from '../data/items';
import { MARKET_ANSWER_MS, type MarketCommand } from '../data/market';
import { useItems } from './items-store';
import { useMarket } from './market-store';

/** How long a step's line saying it was done stays, milliseconds. */
export const CRAFT_DONE_MS = 4000;

/**
 * A workshop command in flight, and what the panel must see to call it done. Accepted, the server answers nothing (refused, it acks): the step shows
 * as the craft or the item read again on the next `items` revision, so that read is where it is done.
 */
export interface CraftSent {
  readonly kind: 'craftStart' | 'craftPoint' | 'craftEnhance' | 'craftFinish' | 'repair' | 'salvage' | 'refine';
  readonly seq: number;
  /** The item repaired or salvaged; `''` for a craft step. */
  readonly item: string;
  /** The item's repairs when the repair was sent. */
  readonly repairs: number;
  /** What the done line names: the schematic, the line a point went to, the item. */
  readonly label: string;
  /** A refine's output, `template:mk`, and how many of it were held when it was sent; absent for the rest. */
  readonly output?: string;
  readonly units?: number;
}

/** What the panel says of the last step: done, or no answer. */
export interface CraftDone {
  readonly text: string;
  readonly ok: boolean;
  readonly seq: number;
}

/**
 * The line saying `sent` landed, or `null` while it has not: the craft before and after a read, and the inventory as read with it.
 *
 * <b>Each step by what it changes</b>, never by the read alone: a loot drop moves the same `items` revision, and must not read as a point spent.
 */
export function craftDoneText(
  sent: CraftSent,
  before: CraftUnderWay | null,
  after: CraftUnderWay | null,
  items: readonly InventoryItem[],
): string | null {
  switch (sent.kind) {
    case 'craftStart':
      return before === null && after !== null
        ? `Craft started: ${sent.label} — ${String(after.pointsLeft)} point(s) to spend`
        : null;
    case 'craftPoint':
      return before !== null && after !== null && after.id === before.id && after.pointsLeft < before.pointsLeft
        ? `+1 to ${sent.label} — ${String(after.pointsLeft)} point(s) left`
        : null;
    case 'craftEnhance':
      return before !== null && after !== null && after.addonMk > before.addonMk
        ? `Add-on Mk ${mkNumeral(after.addonMk)} applied`
        : null;
    case 'craftFinish':
      return before !== null && after === null ? `Finished: ${sent.label} — in your inventory` : null;
    case 'repair': {
      const item = items.find((i) => i.id === sent.item);
      return item?.stats !== undefined && item.stats.repairs > sent.repairs ? `Repaired: ${sent.label}` : null;
    }
    case 'salvage':
      return items.some((i) => i.id === sent.item)
        ? null
        : `Salvaged: ${sent.label} — its materials are in your inventory`;
    case 'refine':
      return unitsOfKey(items, sent.output ?? '') > (sent.units ?? 0) ? `Refined: ${sent.label}` : null;
  }
}

/** Units held of `template:mk`, the grid's stacks only. */
function unitsOfKey(items: readonly InventoryItem[], key: string): number {
  return items
    .filter((i) => i.stats === undefined && i.slot <= 100 && `${String(i.template)}:${String(i.mk)}` === key)
    .reduce((n, i) => n + i.count, 0);
}

/**
 * The crafting panel's state (crafting-build.md C2): the schematics, read once; the craft under way, read again whenever the body's `items` revision
 * moves (every craft step moves it); and the inputs picked for the next start. Commands go out through the bazaar's actions, so their refusals land on
 * its `'craft'` row like any other.
 *
 * <b>A step is done when it shows</b> (Loïc, 2026-11-02: clicks after a step did nothing). The row stays in flight from the send until the read that
 * shows the step, then is settled at once; before, only a refusal or {@link MARKET_ANSWER_MS} cleared it, so every click within 5 s of a step that
 * succeeded was dropped without a word.
 */
export interface CraftStore {
  readonly open: boolean;
  /** Whether the body stands within a crafting station's reach: the panel opens only there, and closes when the body walks away. */
  readonly atStation: boolean;
  readonly book: SchematicBook | null;
  readonly craft: CraftUnderWay | null;
  /** The schematic picked to start, by id. */
  readonly selected: number | null;
  /** Per material or core slot in order, the Mk picked; per part, in order, the instance id picked. */
  readonly mks: readonly number[];
  readonly parts: readonly string[];
  /** The command in flight, until it shows or {@link MARKET_ANSWER_MS} passes. */
  readonly sent: CraftSent | null;
  /** The last step's line, for {@link CRAFT_DONE_MS}. */
  readonly done: CraftDone | null;
  /** Closes the panel, or opens it at a station; away from one, does nothing. */
  readonly toggle: () => void;
  /** Opens the panel at a station: the station's context menu. */
  readonly openPanel: () => void;
  readonly close: () => void;
  /** Where the body stands, every frame: leaving a station's reach closes the panel. */
  readonly setAtStation: (atStation: boolean) => void;
  readonly select: (id: number | null) => void;
  readonly setMk: (index: number, mk: number) => void;
  readonly setPart: (index: number, id: string) => void;
  readonly setChoice: (mks: readonly number[], parts: readonly string[]) => void;
  readonly refresh: () => Promise<void>;
  readonly start: () => void;
  readonly point: (line: number) => void;
  readonly enhance: (mk: number) => void;
  readonly finish: () => void;
  /** Repair, or salvage, an item at the station (crafting-build.md C3). */
  readonly repair: (item: string) => void;
  readonly salvage: (item: string) => void;
  /** Refine `count` of the next Mk from N units each of `template` at `mk` (crafting-catalog.md § 11.1). */
  readonly refine: (template: number, mk: number, count: number, label: string) => void;
  /** The server refused the step in flight: it is not waited for any more. */
  readonly forget: () => void;
  /** A new session: nothing of the last character's craft or picks survives. */
  readonly reset: () => void;
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

/** The latest refresh: an older answer landing after a newer one is dropped. */
let refreshGeneration = 0;
/** Numbers each send and each done line, so a timer only clears its own. */
let sendSeq = 0;

const items = (): readonly InventoryItem[] => useItems.getState().snapshot?.items ?? [];

export const useCraft = create<CraftStore>((set, get) => {
  const say = (text: string, ok: boolean): void => {
    const seq = ++sendSeq;
    set({ done: { text, ok, seq } });
    setTimeout(() => {
      if (get().done?.seq === seq) {
        set({ done: null });
      }
    }, CRAFT_DONE_MS);
  };

  /** Sends `command`, held while one is in flight; what it is for the done line. Unanswered after {@link MARKET_ANSWER_MS}, says so. */
  const issue = (command: MarketCommand, sent: Omit<CraftSent, 'seq'>): void => {
    if (!useMarket.getState().sendCommand(command)) {
      return;
    }

    const seq = ++sendSeq;
    set({ sent: { ...sent, seq } });
    setTimeout(() => {
      if (get().sent?.seq !== seq) {
        return;
      }

      set({ sent: null });
      // A refusal says why on the row already; silence is the one case nothing else reports.
      if (useMarket.getState().rowNotices.craft === undefined) {
        say('No answer from the server — try again', false);
      }
    }, MARKET_ANSWER_MS);
  };

  useMarket.getState().onCraftRefused(() => {
    get().forget();
  });

  const schematicName = (id: number): string => get().book?.schematics.find((x) => x.id === id)?.name ?? 'the craft';

  return {
    open: false,
    atStation: false,
    book: null,
    craft: null,
    selected: null,
    mks: [],
    parts: [],
    sent: null,
    done: null,
    toggle: () => {
      if (get().open) {
        get().close();
      } else {
        get().openPanel();
      }
    },
    openPanel: () => {
      if (!get().open && get().atStation) {
        set({ open: true });
        void get().refresh();
      }
    },
    close: () => {
      set({ open: false });
    },
    setAtStation: (atStation) => {
      if (get().atStation !== atStation) {
        set(atStation ? { atStation } : { atStation, open: false });
      }
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
      const generation = ++refreshGeneration;
      // A repair, a salvage or a refine shows in the inventory, read already: settled now, whatever the craft read below does.
      const early = get().sent;
      if (early !== null && (early.kind === 'repair' || early.kind === 'salvage' || early.kind === 'refine')) {
        const text = craftDoneText(early, null, null, items());
        if (text !== null) {
          set({ sent: null });
          useMarket.getState().settle('craft');
          say(text, true);
        }
      }

      if (get().book === null) {
        const book = parseSchematics(await fetchJson('swg/schematics'));
        if (book !== null) {
          set({ book });
        }
      }

      // A failed read keeps what was shown: `{"craft":null}` says there is none, a failure says nothing.
      const json = await fetchJson('swg/craft');
      if (json === null || generation !== refreshGeneration) {
        return;
      }

      const before = get().craft;
      const after = parseCraft(json);
      set({ craft: after });
      const sent = get().sent;
      const text = sent === null ? null : craftDoneText(sent, before, after, items());
      if (text !== null) {
        set({ sent: null });
        useMarket.getState().settle('craft');
        say(text, true);
      }
    },
    start: () => {
      const { selected, mks, parts } = get();
      if (selected !== null) {
        issue(
          { kind: 'craftStart', schematic: selected, mks, parts },
          { kind: 'craftStart', item: '', repairs: 0, label: schematicName(selected) },
        );
      }
    },
    point: (line) => {
      const craft = get().craft;
      const s = craft === null ? undefined : get().book?.schematics.find((x) => x.id === craft.schematic);
      issue(
        { kind: 'craftPoint', line },
        { kind: 'craftPoint', item: '', repairs: 0, label: s?.lines[line]?.name ?? `line ${String(line + 1)}` },
      );
    },
    enhance: (mk) => {
      issue({ kind: 'craftEnhance', mk }, { kind: 'craftEnhance', item: '', repairs: 0, label: '' });
    },
    finish: () => {
      const craft = get().craft;
      if (craft !== null) {
        issue(
          { kind: 'craftFinish', schematic: craft.schematic },
          { kind: 'craftFinish', item: '', repairs: 0, label: schematicName(craft.schematic) },
        );
      }
    },
    repair: (item) => {
      const held = items().find((i) => i.id === item);
      issue(
        { kind: 'repair', item },
        { kind: 'repair', item, repairs: held?.stats?.repairs ?? 0, label: held?.name ?? 'the item' },
      );
    },
    salvage: (item) => {
      const held = items().find((i) => i.id === item);
      issue({ kind: 'salvage', item }, { kind: 'salvage', item, repairs: 0, label: held?.name ?? 'the item' });
    },
    refine: (template, mk, count, label) => {
      const output = `${String(template)}:${String(mk + 1)}`;
      issue(
        { kind: 'refine', template, mk, count },
        { kind: 'refine', item: '', repairs: 0, label, output, units: unitsOfKey(items(), output) },
      );
    },
    forget: () => {
      sendSeq++;
      set({ sent: null });
    },
    reset: () => {
      refreshGeneration++;
      sendSeq++;
      set({ open: false, atStation: false, craft: null, selected: null, mks: [], parts: [], sent: null, done: null });
    },
  };
});
