import { create } from 'zustand';
import { useChat } from './chat-store';
import type { ChatCategory } from '../data/chat-tabs';
import type { InventoryItem } from '../data/items';
import {
  browseUrl,
  commandRow,
  DEFAULT_QUERY,
  itemWhat,
  newReservationNotices,
  MARKET_ANSWER_MS,
  marketRefusal,
  MAX_LISTING_QTY,
  MAX_UNIT_PRICE,
  parseBrowse,
  parsePrices,
  parseStandard,
  priceOf,
  type BazaarTab,
  type BrowsePage,
  type MarketCommand,
  type MarketListing,
  type MarketMine,
  type MarketPrice,
  type MarketQuery,
  type Reservation,
  type StandardOffer,
} from '../data/market';
import type { RevisionFetch } from '../data/revision-reader';

/**
 * The bazaar on screen (bazaar.md § 6): whether the panel is open and on which tab, the board page browsed, this character's
 * listings and reservations, the price hints, the stack being sold, and — per row — the command in flight and the refusal
 * that came back.
 *
 * <b>Its own store</b>, for the mission store's reason: the board moves at the world's pace. The commands are the app's to
 * send and the requests the app's to make (`ClientApp` registers both), so this file never sees a socket and a test hands in
 * fakes.
 *
 * <b>A refusal stays on its row.</b> The race loser's row is the one a refresh removes — the listing is sold — so a row with
 * a refusal is kept on screen, dimmed, until its text has been read ({@link ROW_NOTICE_MS}).
 */

/** What the app provides the panel. */
export interface MarketActions {
  /** One of the five commands; false when nothing left the client (the command's rate, or a session that may not). */
  readonly send: (command: MarketCommand) => boolean;
  /** How a request is made: `fetch`, or the mock's. */
  readonly fetch: RevisionFetch;
  /** The bearer token of this session's login, or `null` without one. */
  readonly token: () => string | null;
}

/** How long a refusal stays on its row, milliseconds. */
export const ROW_NOTICE_MS = 8000;

/** How often an open Browse tab re-reads its page, milliseconds: the snapshot changes at most twice a second. */
export const BROWSE_REFRESH_MS = 3000;

/** How often the price hints are re-read while the panel is open, milliseconds. */
export const PRICES_REFRESH_MS = 60_000;

/** The stack being sold, and what is asked for it. */
export interface SellForm {
  /** The inventory stack's id, or `null` before one is picked. */
  readonly stack: string | null;
  readonly quantity: number;
  readonly unitPrice: number;
}

export interface MarketState {
  readonly open: boolean;
  readonly tab: BazaarTab;
  /** The region of the bazaar terminal the body stands at — where list, buy, collect and vendor work — or 0 at none. */
  readonly here: number;
  /** The realm the body is in, -1 unknown: twin realms share regions, so a reservation is here only in this realm. */
  readonly hereRealm: number;
  readonly query: MarketQuery;
  readonly page: BrowsePage | null;
  /** Rows a refresh removed while their refusal was still being read: shown dimmed under the page. */
  readonly kept: readonly MarketListing[];
  readonly browseError: string | null;
  readonly mine: MarketMine | null;
  readonly prices: readonly MarketPrice[];
  /** What the vendor sells (crafting-catalog.md § 15.3), read when the standard-issue tab opens. */
  readonly standard: readonly StandardOffer[];
  /** The character's stacks, as the inventory was last read: what can be sold. */
  readonly stacks: readonly InventoryItem[];
  readonly sell: SellForm;
  /** Rows with a command in flight (`commandRow`): their buttons are off, so a second click sends nothing. */
  readonly pending: readonly string[];
  /** Refusals by row, read off the row they were about. */
  readonly rowNotices: Readonly<Partial<Record<string, string>>>;
  readonly actions: MarketActions | null;

  /** Opens the panel on a tab; Browse with `scope` `here` lists the city of the terminal, `all` every city. */
  readonly openPanel: (tab: BazaarTab, scope?: 'here' | 'all') => void;
  readonly close: () => void;
  readonly toggle: () => void;
  readonly setTab: (tab: BazaarTab) => void;
  readonly setHere: (region: number, realm?: number) => void;
  /** Changes the query; back to page 1 unless a page is given. The page is re-read. */
  readonly setQuery: (change: Partial<MarketQuery>) => void;
  /** Re-reads the page now. */
  readonly refreshBrowse: () => Promise<void>;
  readonly refreshPrices: () => Promise<void>;
  readonly refreshStandard: () => Promise<void>;
  /** Called every frame: re-reads what is due while the panel is open. */
  readonly tick: (nowMs: number) => void;
  readonly setMine: (mine: MarketMine) => void;
  readonly setStacks: (stacks: readonly InventoryItem[]) => void;
  /** Picks a stack to sell: all of it, at its market value (or the lowest listed when it has none). */
  readonly selectStack: (stackId: string) => void;
  readonly setSellQuantity: (quantity: number) => void;
  readonly setSellPrice: (unitPrice: number) => void;
  readonly buy: (listing: MarketListing) => void;
  readonly cancel: (id: string) => void;
  /** Collects one reservation, or `'0'` every one this city holds. */
  readonly collect: (id: string) => void;
  /** Lists the stack picked, as the Sell form says. */
  readonly list: () => void;
  /** Sells the stack picked to the vendor, the form's quantity. */
  readonly vendor: () => void;
  /** Buys standard issue from the vendor at the price shown (crafting-catalog.md § 15.3): one item, or a quantity of a material. */
  readonly buyStandard: (template: number, price: number, quantity?: number) => void;
  /** Wears an item with stats (crafting-build.md § 2.2); its refusal shows on the `'gear'` row. */
  readonly equip: (itemId: string) => void;
  /** Sends a command as the panels' buttons do: held while one of its row is in flight, its refusal shown on its row. False when not sent. */
  readonly sendCommand: (command: MarketCommand) => boolean;
  /**
   * A row's command was seen to land — an accepted command is never acked, so the caller that saw its effect says so: its buttons are back at once
   * rather than after {@link MARKET_ANSWER_MS}.
   */
  readonly settle: (row: string) => void;
  /** Who hears that the workshop's row was refused: the crafting store, which forgets the step it waited for. */
  readonly onCraftRefused: (handler: (() => void) | null) => void;
  /** Who hears a waypoint command refused (client.md C38): the waypoints store, which says why in chat — the commands are typed there. */
  readonly onWaypointRefused: (handler: ((text: string) => void) | null) => void;
  /** Takes off what is worn in an equip slot, 101-107. */
  readonly unequip: (slot: number) => void;
  /** Packs an item or a crate into an identical one; takes items out of a crate (crafting-catalog.md § 11.2). Refusals show on the `'gear'` row. */
  readonly pack: (item: string, into: string) => void;
  readonly unpack: (item: string, count: number) => void;
  /** Moves `count` of a stack into grid slot `slot` (0: the lowest free), and throws `count` of one away (inventory-loot.md § 10). Refusals on `'gear'`. */
  readonly move: (item: string, slot: number, count: number) => void;
  readonly discard: (item: string, count: number) => void;
  /** The server refused `command` with `reason`. */
  readonly refused: (reason: number, command: MarketCommand) => void;
  /**
   * What a wallet `sale` row of `amount` credits was about, for its chat line: a listing of this character's that sold
   * (`amount` > 0), or one it bought (< 0) — found by the row's `ref`, the listing's id, or else by its total; `null` when it
   * does not know. Each is told once.
   */
  readonly saleItem: (amount: number, ref?: string) => string | null;
  /** Forgets everything of the session: a new session may be another character. */
  readonly reset: () => void;
  readonly setActions: (actions: MarketActions | null) => void;
}

/** Listings this character listed or bought, by id: what a wallet row of their total was about. */
const sold = new Map<string, { readonly what: string; readonly total: number }>();
const bought = new Map<string, { readonly what: string; readonly total: number }>();
/** The listings a buy was sent for, by id: a refused one is shown again, with its reason, if a refresh already dropped it. */
const asked = new Map<string, MarketListing>();

let browseGeneration = 0;
let browseAtMs = Number.NEGATIVE_INFINITY;
let pricesAtMs = Number.NEGATIVE_INFINITY;
let lastNowMs = 0;

const EMPTY_SELL: SellForm = { stack: null, quantity: 1, unitPrice: 1 };

/**
 * The rows whose commands a context menu sends from the world — an installation's, a kit placed (client.md C42): their refusals are said in the chat,
 * as no window shows them.
 */
const CHAT_ROWS: ReadonlyMap<string, ChatCategory> = new Map<string, ChatCategory>([
  ['install', 'structures'],
  // A /tell's refusal, and a mail command's (mail.md § 6): sent from the chat or the mail window, said in the Tells category.
  ['tell', 'tells'],
  ['mail', 'tells'],
]);

/**
 * The rows a command does not hold in flight: a mail's read or delete is idempotent and the window answers them at once, and a /tell is held by the server's
 * own rate — guarded, a second one inside the answer's 5 s, or a delete straight after a read, was dropped without a word (review, 2026-11-04).
 */
const UNHELD_ROWS: ReadonlySet<string> = new Set(['mail', 'tell']);

export const useMarket = create<MarketState>((set, get) => {
  /** Sends `command` for `row`, marks the row in flight for {@link MARKET_ANSWER_MS}, clears its old refusal. */
  const send = (command: MarketCommand): boolean => {
    const row = commandRow(command);
    const held = !UNHELD_ROWS.has(row);
    if (held && get().pending.includes(row)) {
      return false;
    }

    const sent = get().actions?.send(command) ?? false;
    if (!sent) {
      const text = 'Not sent — wait a moment and try again';
      set({ rowNotices: { ...get().rowNotices, [row]: text } });
      const category = CHAT_ROWS.get(row);
      if (category !== undefined) {
        useChat.getState().notice(text, category);
      }

      return false;
    }

    if (!held) {
      set({ rowNotices: omit(get().rowNotices, row) });
      return true;
    }

    set({ pending: [...get().pending, row], rowNotices: omit(get().rowNotices, row) });
    // This send's own: a row settled early and sent again is not cleared by the first send's timer (review, 2026-11-03).
    const seq = (sendSeqOf.get(row) ?? 0) + 1;
    sendSeqOf.set(row, seq);
    setTimeout(() => {
      if (sendSeqOf.get(row) === seq) {
        clearPending(row);
      }
    }, MARKET_ANSWER_MS);
    return true;
  };

  /** Told when the workshop's row is refused — the crafting store's `forget`, set by it (the two stores import one way only). */
  let refusedCraft: (() => void) | null = null;
  let refusedWaypoint: ((text: string) => void) | null = null;

  /** The last send of each row, by sequence. */
  const sendSeqOf = new Map<string, number>();

  const clearPending = (row: string): void => {
    if (get().pending.includes(row)) {
      set({ pending: get().pending.filter((r) => r !== row) });
    }
  };

  const fetchJson = async (url: string): Promise<unknown> => {
    const actions = get().actions;
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

  return {
    open: false,
    tab: 'browse',
    here: 0,
    hereRealm: -1,
    query: DEFAULT_QUERY,
    page: null,
    kept: [],
    browseError: null,
    mine: null,
    prices: [],
    standard: [],
    stacks: [],
    sell: EMPTY_SELL,
    pending: [],
    rowNotices: {},
    actions: null,

    openPanel: (tab, scope) => {
      const query =
        scope === undefined
          ? get().query
          : { ...get().query, city: scope === 'here' ? get().here : 0, realm: get().hereRealm, page: 1 };
      set({ open: true, tab, query });
      void get().refreshBrowse();
      if (lastNowMs - pricesAtMs > PRICES_REFRESH_MS || get().prices.length === 0) {
        void get().refreshPrices();
      }
    },
    close: () => {
      if (get().open) {
        set({ open: false });
      }
    },
    toggle: () => {
      if (get().open) {
        get().close();
      } else {
        get().openPanel(get().tab);
      }
    },
    setTab: (tab) => {
      set({ tab });
      if (tab === 'browse') {
        void get().refreshBrowse();
      }
    },
    setHere: (here, hereRealm = -1) => {
      if (get().here !== here || get().hereRealm !== hereRealm) {
        set({ here, hereRealm });
        // The bazaar is used at its terminal only (client.md C40): walked away from it, the panel closes, as the crafting panel does.
        if (here === 0 && get().open) {
          get().close();
        }
      }
    },
    setQuery: (change) => {
      set({ query: { ...get().query, page: 1, ...change } });
      void get().refreshBrowse();
    },
    refreshBrowse: async () => {
      const generation = ++browseGeneration;
      browseAtMs = lastNowMs;
      const answer = parseBrowse(await fetchJson(browseUrl(get().query)));
      if (generation !== browseGeneration) {
        return; // a newer query was asked meanwhile
      }

      if (answer === null) {
        set({ browseError: 'The board could not be read — retrying' });
        return;
      }

      // A row with a refusal still showing that this page no longer lists: kept, dimmed, so the reason stays readable.
      const notices = get().rowNotices;
      const listed = new Set(answer.listings.map((l) => l.id));
      const before = [...(get().page?.listings ?? []), ...get().kept];
      const kept = before.filter((l) => notices[l.id] !== undefined && !listed.has(l.id));
      // A buy in flight whose listing left the board has landed — or been refused, which says so on its row.
      const gone = new Set(before.filter((l) => !listed.has(l.id)).map((l) => l.id));
      set({ page: answer, kept, browseError: null, pending: get().pending.filter((r) => !gone.has(r)) });
    },
    refreshStandard: async () => {
      const standard = parseStandard(await fetchJson('swg/market/standard'));
      if (standard !== null) {
        set({ standard });
      }
    },
    refreshPrices: async () => {
      pricesAtMs = lastNowMs;
      const prices = parsePrices(await fetchJson('swg/market/prices'));
      if (prices !== null) {
        set({ prices });
      }
    },
    tick: (nowMs) => {
      lastNowMs = nowMs;
      const s = get();
      if (!s.open) {
        return;
      }

      if (s.tab === 'browse' && nowMs - browseAtMs >= BROWSE_REFRESH_MS) {
        void s.refreshBrowse();
      }

      if (nowMs - pricesAtMs >= PRICES_REFRESH_MS) {
        void s.refreshPrices();
      }
    },
    setMine: (mine) => {
      for (const l of mine.listed) {
        sold.set(l.id, { what: itemWhat(l.name, l.mk, l.qty), total: l.total });
      }

      // Goods newly waiting at a bazaar are said in chat: no toolbar button counts them (client.md C40).
      for (const text of newReservationNotices(get().mine, mine)) {
        useChat.getState().notice(text, 'economy');
      }

      // Every change the server makes moves this read: whatever was in flight has landed, or been refused.
      set({ mine, pending: get().pending.filter((row) => row === 'sell' || row === 'all') });
      if (get().open && get().tab === 'browse') {
        void get().refreshBrowse();
      }
    },
    setStacks: (stacks) => {
      const sell = get().sell;
      // The inventory moved: a list or a vendor sale in flight has landed (or was refused, which said so).
      const pending = get().pending.filter((row) => row !== 'sell' && row !== 'all');
      const picked = sell.stack === null ? undefined : stacks.find((s) => s.id === sell.stack);
      set({
        stacks,
        pending,
        sell:
          picked === undefined
            ? sell.stack === null
              ? sell
              : EMPTY_SELL
            : { ...sell, quantity: Math.min(sell.quantity, picked.count) },
      });
    },
    selectStack: (stackId) => {
      const stack = get().stacks.find((s) => s.id === stackId);
      if (stack === undefined) {
        return;
      }

      const price = priceOf(get().prices, stack.item, stack.mk);
      // The value is decimal credits (a material's 1.7): a price is whole credits.
      const unitPrice = Math.max(1, price === undefined ? 1 : Math.round(price.value > 0 ? price.value : price.lowest));
      set({
        sell: { stack: stackId, quantity: Math.min(stack.count, MAX_LISTING_QTY), unitPrice },
        rowNotices: omit(get().rowNotices, 'sell'),
      });
    },
    setSellQuantity: (quantity) => {
      const stack = get().stacks.find((s) => s.id === get().sell.stack);
      const max = Math.min(MAX_LISTING_QTY, stack?.count ?? MAX_LISTING_QTY);
      // An item with stats, or a crate of them, goes whole.
      const min = stack?.stats === undefined ? 1 : max;
      set({ sell: { ...get().sell, quantity: clampInt(quantity, min, max) } });
    },
    setSellPrice: (unitPrice) => {
      set({ sell: { ...get().sell, unitPrice: clampInt(unitPrice, 1, MAX_UNIT_PRICE) } });
    },
    buy: (listing) => {
      if (send({ kind: 'buy', listing: listing.id, unitPrice: listing.unit })) {
        bought.set(listing.id, { what: itemWhat(listing.name, listing.mk, listing.qty), total: listing.total });
        asked.set(listing.id, listing);
      }
    },
    cancel: (id) => {
      send({ kind: 'cancel', listing: id });
    },
    collect: (id) => {
      send({ kind: 'collect', listing: id });
    },
    list: () => {
      const { sell, stacks } = get();
      const stack = stacks.find((s) => s.id === sell.stack);
      if (stack === undefined) {
        return;
      }

      if (stack.template === 0) {
        set({ rowNotices: { ...get().rowNotices, sell: 'This server does not name item templates — cannot list' } });
        return;
      }

      // An item with stats is listed by its id, one at a time (crafting-build.md § 2.1) — a crate whole, its count the quantity.
      send({
        kind: 'list',
        template: stack.template,
        mk: stack.mk,
        quantity: stack.stats === undefined ? sell.quantity : stack.count,
        unitPrice: sell.unitPrice,
        ...(stack.stats === undefined ? {} : { item: stack.id }),
      });
    },
    vendor: () => {
      const { sell, stacks } = get();
      const stack = stacks.find((s) => s.id === sell.stack);
      if (stack === undefined) {
        return;
      }

      if (stack.template === 0) {
        set({ rowNotices: { ...get().rowNotices, sell: 'This server does not name item templates — cannot sell' } });
        return;
      }

      send(
        stack.stats === undefined
          ? { kind: 'vendor', template: stack.template, mk: stack.mk, quantity: sell.quantity }
          : { kind: 'vendor', template: stack.template, mk: stack.mk, quantity: stack.count, item: stack.id },
      );
    },
    buyStandard: (template, price, quantity) => {
      send(
        quantity === undefined
          ? { kind: 'standard', template, price }
          : { kind: 'standard', template, price, quantity },
      );
    },
    sendCommand: (command) => send(command),
    settle: (row) => {
      clearPending(row);
    },
    onWaypointRefused: (handler) => {
      refusedWaypoint = handler;
    },
    onCraftRefused: (handler) => {
      refusedCraft = handler;
    },
    equip: (itemId) => {
      send({ kind: 'equip', item: itemId });
    },
    unequip: (slot) => {
      send({ kind: 'unequip', slot });
    },
    pack: (item, into) => {
      send({ kind: 'pack', item, into });
    },
    unpack: (item, count) => {
      send({ kind: 'unpack', item, count });
    },
    move: (item, slot, count) => {
      send({ kind: 'move', item, slot, count });
    },
    discard: (item, count) => {
      send({ kind: 'discard', item, count });
    },
    refused: (reason, command) => {
      const row = commandRow(command);
      const city =
        command.kind === 'collect' ? (get().mine?.reserved.find((r) => r.id === command.listing)?.city ?? 0) : 0;
      let kept = get().kept;
      if (command.kind === 'buy') {
        bought.delete(command.listing);
        // The race loser's row may be gone already — the board refreshed between the buy and its refusal — and the reason
        // must still be read on the row that was clicked.
        const clicked = asked.get(command.listing);
        asked.delete(command.listing);
        const shown = get().page?.listings.some((l) => l.id === command.listing) === true;
        if (clicked !== undefined && !shown && !kept.some((l) => l.id === command.listing)) {
          kept = [...kept, clicked];
        }
      }

      const text = marketRefusal(reason, city);
      // The workshop's step refused is not waited for: a later inventory read must not report it done (review, 2026-11-03).
      if (row === 'craft') {
        refusedCraft?.();
      }

      if (row === 'waypoint') {
        refusedWaypoint?.(text);
      }

      // Sent from a menu in the world, which has no window to say it in (client.md C42).
      const category = CHAT_ROWS.get(row);
      if (category !== undefined) {
        useChat.getState().notice(text, category);
      }

      set({
        pending: get().pending.filter((r) => r !== row),
        rowNotices: { ...get().rowNotices, [row]: text },
        kept,
      });
      setTimeout(() => {
        if (get().rowNotices[row] === text) {
          set({ rowNotices: omit(get().rowNotices, row), kept: get().kept.filter((l) => l.id !== row) });
        }
      }, ROW_NOTICE_MS);
    },
    saleItem: (amount, ref) => {
      const book = amount > 0 ? sold : bought;
      const named = ref === undefined ? undefined : book.get(ref);
      if (ref !== undefined && named !== undefined) {
        book.delete(ref);
        return named.what;
      }

      const total = Math.abs(amount);
      for (const [id, entry] of book) {
        if (entry.total === total) {
          book.delete(id);
          return entry.what;
        }
      }

      return null;
    },
    reset: () => {
      sold.clear();
      bought.clear();
      asked.clear();
      browseGeneration++;
      set({
        open: false,
        here: 0,
        hereRealm: -1,
        page: null,
        kept: [],
        mine: null,
        stacks: [],
        sell: EMPTY_SELL,
        pending: [],
        rowNotices: {},
        browseError: null,
      });
    },
    setActions: (actions) => {
      set({ actions });
    },
  };
});

/** `record` without `key`. */
function omit(record: Readonly<Partial<Record<string, string>>>, key: string): Partial<Record<string, string>> {
  return Object.fromEntries(Object.entries(record).filter(([k]) => k !== key));
}

/** Whether a reservation can be collected where the body stands: a terminal of its city, in its realm when both are known. */
export function collectableHere(r: Pick<Reservation, 'city' | 'realm'>, here: number, hereRealm: number): boolean {
  return here > 0 && r.city === here && (r.realm < 0 || hereRealm < 0 || r.realm === hereRealm);
}

function clampInt(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, Math.floor(value))) : min;
}
