import { create } from 'zustand';
import type { InventoryItem } from '../data/items';
import {
  browseUrl,
  commandRow,
  DEFAULT_QUERY,
  itemWhat,
  MARKET_ANSWER_MS,
  marketRefusal,
  MAX_LISTING_QTY,
  MAX_UNIT_PRICE,
  parseBrowse,
  parsePrices,
  priceOf,
  type BazaarTab,
  type BrowsePage,
  type MarketCommand,
  type MarketListing,
  type MarketMine,
  type MarketPrice,
  type MarketQuery,
  type Reservation,
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
  /** The server refused `command` with `reason`. */
  readonly refused: (reason: number, command: MarketCommand) => void;
  /**
   * What a wallet `sale` row of `amount` credits was about, for its chat line: a listing of this character's that sold
   * (`amount` > 0), or one it bought (< 0); `null` when it does not know. Each is told once.
   */
  readonly saleItem: (amount: number) => string | null;
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

export const useMarket = create<MarketState>((set, get) => {
  /** Sends `command` for `row`, marks the row in flight for {@link MARKET_ANSWER_MS}, clears its old refusal. */
  const send = (command: MarketCommand): boolean => {
    const row = commandRow(command);
    if (get().pending.includes(row)) {
      return false;
    }

    const sent = get().actions?.send(command) ?? false;
    if (!sent) {
      set({ rowNotices: { ...get().rowNotices, [row]: 'Not sent — wait a moment and try again' } });
      return false;
    }

    set({ pending: [...get().pending, row], rowNotices: omit(get().rowNotices, row) });
    setTimeout(() => {
      clearPending(row);
    }, MARKET_ANSWER_MS);
    return true;
  };

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
    stacks: [],
    sell: EMPTY_SELL,
    pending: [],
    rowNotices: {},
    actions: null,

    openPanel: (tab, scope) => {
      const query =
        scope === undefined ? get().query : { ...get().query, city: scope === 'here' ? get().here : 0, page: 1 };
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
      const unitPrice = Math.max(1, price === undefined ? 1 : price.value > 0 ? price.value : price.lowest);
      set({
        sell: { stack: stackId, quantity: Math.min(stack.count, MAX_LISTING_QTY), unitPrice },
        rowNotices: omit(get().rowNotices, 'sell'),
      });
    },
    setSellQuantity: (quantity) => {
      const stack = get().stacks.find((s) => s.id === get().sell.stack);
      const max = Math.min(MAX_LISTING_QTY, stack?.count ?? MAX_LISTING_QTY);
      set({ sell: { ...get().sell, quantity: clampInt(quantity, 1, max) } });
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

      send({
        kind: 'list',
        template: stack.template,
        mk: stack.mk,
        quantity: sell.quantity,
        unitPrice: sell.unitPrice,
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

      send({ kind: 'vendor', template: stack.template, mk: stack.mk, quantity: sell.quantity });
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
    saleItem: (amount) => {
      const book = amount > 0 ? sold : bought;
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
