import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Descriptor } from '../src/assets/descriptor';
import { BAZAAR_INTERACTION, bazaarMenuItems, bazaarTerminalOf, canUseAt } from '../src/app/ticket-terminal';
import { panelKey } from '../src/app/panel-keys';
import { parseInventory, type InventoryItem } from '../src/data/items';
import {
  browseUrl,
  cityName,
  commandRow,
  DEFAULT_QUERY,
  feeLine,
  fees,
  itemWhat,
  MARKET_ANSWER_MS,
  MARKET_REASONS,
  marketRefusal,
  marketWalletLine,
  marketWire,
  MarketReason,
  parseBrowse,
  parseMine,
  parsePrices,
  regionAt,
  vendorPayout,
  type MarketCommand,
  type MarketListing,
} from '../src/data/market';
import { MockInventory, MOCK_DROP_MS } from '../src/data/mock/inventory';
import { CONTESTED_NAME, MOCK_BAZAAR_REGION, MOCK_CREDITS, MOCK_SALE_MS, MockMarket } from '../src/data/mock/market';
import { parseWallet } from '../src/data/wallet';
import { CITIES } from '../src/data/world-data';
import { useChat } from '../src/state/chat-store';
import { collectableHere, ROW_NOTICE_MS, useMarket, type MarketActions } from '../src/state/market-store';
import { useUi } from '../src/state/ui-store';
import { useWallet } from '../src/state/wallet-store';

/**
 * The bazaar on the client (bazaar.md § 6, BZ-14): the four answers checked, the fees and the vendor's payout as the desk
 * computes them, the refusal reasons, the five commands on the wire, the store's rows (in flight, refused, kept), the
 * terminal's menu, and the mock's desk — the same rules the panel is exercised against without a server.
 */

// `isTyping` tests `target instanceof HTMLInputElement`, which throws when the global is undefined (camera-input.test.ts).
{
  const target = globalThis as unknown as Record<string, unknown>;
  const absent = function Absent(): void {};
  target['HTMLInputElement'] ??= absent;
  target['HTMLSelectElement'] ??= absent;
  target['HTMLTextAreaElement'] ??= absent;
}

const listing = (id: string, over: Partial<MarketListing> = {}): MarketListing => ({
  id,
  city: 1,
  cityName: CITIES[0].name,
  realm: 0,
  item: 'mat_hide',
  name: 'Hide',
  category: 'material',
  template: 8,
  mk: 2,
  qty: 20,
  unit: 3,
  total: 60,
  seller: '17',
  sellerName: 'Kessa',
  ageS: 40,
  ...over,
});

describe('the fees', () => {
  it('are the desk formulas: 1 % listed (at least 1), 8 % taxed at the sale, the rest the seller’s', () => {
    expect(fees(900)).toEqual({ listFee: 9, tax: 72, net: 828 });
    expect(fees(60)).toEqual({ listFee: 1, tax: 4, net: 56 });
    // The minimum fee is the anti-spam floor; a cheap sale is taxed nothing (§ 13).
    expect(fees(1)).toEqual({ listFee: 1, tax: 0, net: 1 });
    expect(fees(12)).toEqual({ listFee: 1, tax: 0, net: 12 });
    expect(fees(13).tax).toBe(1);
    expect(fees(99 * 1_000_000)).toEqual({ listFee: 990_000, tax: 7_920_000, net: 91_080_000 });
  });

  it('are previewed in one line', () => {
    expect(feeLine(900)).toBe('fee 9 cr now · tax 72 cr at sale · you get 828');
  });

  it('pay the vendor ⌊count × value × 20 %⌋ from the per-unit milli-credits /prices states', () => {
    // A Hide Mk I is 1.7 cr: the vendor pays 0.34 cr a unit.
    expect(vendorPayout(20, 340)).toBe(6);
    expect(vendorPayout(2, 340)).toBe(0);
    expect(vendorPayout(1, 145_200)).toBe(145);
    expect(vendorPayout(5, 0)).toBe(0);
  });
});

describe('a refusal', () => {
  it('reads every reason of § 3.1, 151-163 in order, and NotInWorld reused', () => {
    expect(Object.keys(MARKET_REASONS).map(Number)).toEqual([142, ...Array.from({ length: 13 }, (_, i) => 151 + i)]);
    expect(marketRefusal(MarketReason.Sold)).toBe('Sold to someone else first');
    expect(marketRefusal(MarketReason.NotEnough)).toBe('Not enough credits');
    expect(marketRefusal(MarketReason.NotAtBazaar)).toContain('bazaar terminal');
    expect(marketRefusal(MarketReason.Worthless)).toContain('vendor');
    expect(marketRefusal(999)).toBe('Refused (999)');
  });

  it('names the city a reservation must be collected at', () => {
    expect(marketRefusal(MarketReason.WrongCity, 3)).toBe(`Collect it at ${CITIES[2].name}`);
    expect(marketRefusal(MarketReason.WrongCity)).toBe('Collect it at its city');
  });
});

describe('a command', () => {
  it('goes on the wire by its catalog name and codec fields, a 64-bit id as a bigint', () => {
    expect(marketWire({ kind: 'list', template: 8, mk: 2, quantity: 20, unitPrice: 3 })).toEqual({
      name: 'MarketList',
      values: { template: 8, mk: 2, quantity: 20, unitPrice: 3 },
    });
    expect(marketWire({ kind: 'buy', listing: '18446744073709551615', unitPrice: 3 })).toEqual({
      name: 'MarketBuy',
      values: { listing: 18446744073709551615n, unitPrice: 3 },
    });
    expect(marketWire({ kind: 'cancel', listing: '42' })?.name).toBe('MarketCancel');
    expect(marketWire({ kind: 'collect', listing: '0' })).toEqual({ name: 'MarketCollect', values: { listing: 0n } });
    expect(marketWire({ kind: 'vendor', template: 14, mk: 1, quantity: 1 })?.name).toBe('SellToVendor');
    expect(marketWire({ kind: 'cancel', listing: 'x' })).toBeNull();
  });

  it('is about a row: its listing, all, or the Sell tab', () => {
    expect(commandRow({ kind: 'buy', listing: '7', unitPrice: 1 })).toBe('7');
    expect(commandRow({ kind: 'collect', listing: '0' })).toBe('all');
    expect(commandRow({ kind: 'vendor', template: 8, mk: 1, quantity: 1 })).toBe('sell');
  });
});

describe('the answers', () => {
  it('read a browse page, a 64-bit id kept as text, a malformed row dropped', () => {
    const page = parseBrowse({
      v: 9,
      city: 1,
      total: 2,
      page: 1,
      pages: 1,
      listings: [
        {
          id: '9007199254740993',
          city: 1,
          item: 'mat_hide',
          name: 'Hide',
          category: 'material',
          mk: 2,
          qty: 20,
          unit: 3,
          total: 60,
          seller: 17,
          sellerName: 'Kessa',
          ageS: 40,
        },
        { id: 'bad', qty: 1, unit: 1 },
      ],
    });
    expect(page?.listings).toHaveLength(1);
    expect(page?.listings[0]).toMatchObject({ id: '9007199254740993', seller: '17', template: 0, total: 60 });
    // Without the server's city name and realm: the region's name from the city table, the realm unknown.
    expect(page?.listings[0]).toMatchObject({ cityName: CITIES[0].name, realm: -1 });
    expect(parseBrowse({ listings: {} })).toBeNull();
    expect(parseBrowse(null)).toBeNull();
  });

  it('read /mine: listed with its fee and ticks, reserved with their state, the caps, the city as the server names it', () => {
    const mine = parseMine({
      listed: [
        {
          id: '5',
          city: 2,
          cityName: 'Whitestone II',
          realm: 3,
          item: 'core_armor',
          name: 'Armor Core',
          category: 'core',
          mk: 3,
          qty: 1,
          unit: 900,
          total: 900,
          fee: 9,
          listedTick: 100,
          expireTick: 1_728_100,
          ageS: 5,
        },
      ],
      reserved: [
        { id: '6', city: 3, item: 'mat_bone', name: 'Bone', mk: 1, qty: 9, unit: 2, state: 'sold', closedTick: 100 },
        { id: '7', city: 3, item: 'mat_bone', name: 'Bone', mk: 1, qty: 9, unit: 2, state: 'gone', closedTick: 1 },
      ],
      caps: { listings: 20, reserved: 20 },
    });
    expect(mine?.listed[0]).toMatchObject({
      total: 900,
      fee: 9,
      expireTick: 1_728_100,
      cityName: 'Whitestone II',
      realm: 3,
    });
    expect(mine?.reserved.map((r) => r.state)).toEqual(['sold']);
    expect(mine?.caps).toEqual({ listings: 20, reserved: 20 });
    expect(parseMine({ listed: [] })).toBeNull();
  });

  it('read /prices', () => {
    const prices = parsePrices({
      v: 4,
      prices: [
        {
          item: 'mat_hide',
          name: 'Hide',
          category: 'material',
          mk: 1,
          value: 1,
          vendor: 340,
          listed: 40,
          lowest: 2,
          median: 2,
          min: 1,
          max: 3,
          sales: 5,
        },
      ],
    });
    expect(prices?.[0]).toMatchObject({ item: 'mat_hide', name: 'Hide', vendor: 340, lowest: 2, min: 1, max: 3 });
    expect(parsePrices([])).toEqual([]);
    expect(parsePrices({})).toBeNull();
  });

  it('ask for only the filters that are set', () => {
    expect(browseUrl(DEFAULT_QUERY)).toBe('swg/market?city=0&sort=price&page=1');
    expect(browseUrl({ ...DEFAULT_QUERY, city: 2, cat: 'core', mk: 3, max: 500, sort: '-price', page: 2 })).toBe(
      'swg/market?city=2&cat=core&mk=3&max=500&sort=-price&page=2',
    );
  });
});

describe('a city', () => {
  it('is a region, the city index + 1, its name the server’s order', () => {
    expect(cityName(1)).toBe(CITIES[0].name);
    expect(cityName(0)).toBe('');
    expect(regionAt(CITIES[3].x + 40, CITIES[3].z - 10)).toBe(4);
  });
});

describe('a reservation', () => {
  it('is collected at a terminal of its city, in its realm when both are known (twins share regions)', () => {
    expect(collectableHere({ city: 2, realm: 0 }, 2, 0)).toBe(true);
    expect(collectableHere({ city: 2, realm: 1 }, 2, 0)).toBe(false);
    expect(collectableHere({ city: 2, realm: -1 }, 2, 0)).toBe(true);
    expect(collectableHere({ city: 3, realm: 0 }, 2, 0)).toBe(false);
    expect(collectableHere({ city: 2, realm: 0 }, 0, 0)).toBe(false);
  });
});

describe('a wallet line', () => {
  it('says a sale from both sides and the vendor, nothing for a fee or a tax alone', () => {
    expect(marketWalletLine('sale', 60, 'Bob', 4, 'Hide Mk II ×20')).toBe(
      'Sold Hide Mk II ×20 to Bob for 60 cr (tax 4)',
    );
    expect(marketWalletLine('sale', -60, 'Ann', 0, 'Hide Mk II ×20')).toBe('Bought Hide Mk II ×20 from Ann for 60 cr');
    expect(marketWalletLine('sale', 60, 'Bob', 4, null)).toBe('Sold to Bob for 60 cr (tax 4)');
    expect(marketWalletLine('vendor', 145, 'World', 0, null)).toBe('Vendor paid 145 cr');
    expect(marketWalletLine('listfee', -1, 'World', 0, null)).toBeNull();
    expect(itemWhat('Armor Core', 3, 1)).toBe('Armor Core Mk III');
  });
});

describe('a bazaar terminal', () => {
  it("is used from its published 'bazaar' interaction, from any side within reach_m", () => {
    const terminal = {
      id: 'bazaar_terminal',
      interactions: [
        { kind: BAZAAR_INTERACTION, pos: [0, 0, 1.1], rot: [0, 0, 0, 1], forward: '-Z', radius: 1, reach_m: 3.3 },
      ],
      children: [],
      bounds: {
        render: [
          [-0.6, 0, -0.5],
          [0.6, 1.8, 0.5],
        ],
        collision: null,
      },
    } as unknown as Descriptor;
    const use = bazaarTerminalOf(terminal, 100, 0, 200, 0);
    expect(use).not.toBeNull();
    expect(canUseAt(use!, 100, 203.2)).toBe(true);
    expect(canUseAt(use!, 100, 196.8)).toBe(true);
    expect(canUseAt(use!, 104, 200)).toBe(false);
    expect(bazaarTerminalOf({ ...terminal, interactions: [] }, 0, 0, 0, 0)).toBeNull();
  });

  it('offers the board here and everywhere, selling, the vendor, its listings and the reserved count', () => {
    const items = bazaarMenuItems(2);
    expect(items.map((i) => i.label)).toEqual([
      'Browse this city',
      'Browse all cities',
      'Sell…',
      'Sell to vendor…',
      'My listings',
      'Reserved (2)',
    ]);
    expect(items[0].action).toEqual({ kind: 'bazaar', tab: 'browse', scope: 'here' });
    expect(bazaarMenuItems(0)[5].label).toBe('Reserved');
  });

  it('is toggled by B', () => {
    const key = {
      key: 'b',
      repeat: false,
      isComposing: false,
      ctrlKey: false,
      altKey: false,
      metaKey: false,
      target: null,
    };
    expect(panelKey(key as unknown as KeyboardEvent)).toBe('bazaar');
  });
});

/** The store, against fake actions: what was sent, and a board the test answers. */
function harness(board: MarketListing[] = [listing('1'), listing('2', { unit: 5, total: 100 })]) {
  const sent: MarketCommand[] = [];
  let accept = true;
  let answer: unknown = { v: 1, city: 0, total: board.length, page: 1, pages: 1, listings: board };
  const actions: MarketActions = {
    send: (command) => {
      if (accept) {
        sent.push(command);
      }

      return accept;
    },
    fetch: (url) =>
      Promise.resolve({
        ok: true,
        status: 200,
        json: () =>
          Promise.resolve(
            url.startsWith('swg/market/prices') ? { prices: [] } : url.startsWith('swg/market?') ? answer : null,
          ),
      }),
    token: () => 't',
  };
  useMarket.getState().setActions(actions);
  return {
    sent,
    refuse: () => {
      accept = false;
    },
    answer: (listings: MarketListing[]) => {
      answer = { v: 2, city: 0, total: listings.length, page: 1, pages: 1, listings };
    },
  };
}

const flush = async (): Promise<void> => {
  for (let i = 0; i < 6; i++) {
    await Promise.resolve();
  }
};

describe('the market store', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useMarket.getState().reset();
    useChat.getState().clear();
    useWallet.getState().resetWallet();
  });

  afterEach(() => {
    vi.useRealTimers();
    useMarket.getState().setActions(null);
  });

  it('opens on the city of the terminal, or every city, and reads the board', async () => {
    harness();
    useMarket.getState().setHere(3);
    useMarket.getState().openPanel('browse', 'here');
    expect(useMarket.getState().query.city).toBe(3);
    await flush();
    expect(useMarket.getState().page?.listings.map((l) => l.id)).toEqual(['1', '2']);
    useMarket.getState().openPanel('browse', 'all');
    expect(useMarket.getState().query.city).toBe(0);
  });

  it('sends a buy at the price shown, its row waiting, a second click sending nothing', () => {
    const h = harness();
    useMarket.getState().buy(listing('1'));
    useMarket.getState().buy(listing('1'));
    expect(h.sent).toEqual([{ kind: 'buy', listing: '1', unitPrice: 3 }]);
    expect(useMarket.getState().pending).toEqual(['1']);
    // Accepted, nothing answers: the row is let go after the answer window.
    vi.advanceTimersByTime(MARKET_ANSWER_MS);
    expect(useMarket.getState().pending).toEqual([]);
  });

  it('shows the race loser "Sold to someone else first" on the row it clicked, kept while the text is read', async () => {
    const h = harness();
    useMarket.getState().openPanel('browse');
    await flush();
    useMarket.getState().buy(listing('1'));
    useMarket.getState().refused(MarketReason.Sold, { kind: 'buy', listing: '1', unitPrice: 3 });
    expect(useMarket.getState().rowNotices['1']).toBe('Sold to someone else first');
    expect(useMarket.getState().pending).toEqual([]);

    // The board no longer lists it — it was sold — but the row stays, dimmed, with its reason.
    h.answer([listing('2', { unit: 5, total: 100 })]);
    await useMarket.getState().refreshBrowse();
    expect(useMarket.getState().kept.map((l) => l.id)).toEqual(['1']);
    vi.advanceTimersByTime(ROW_NOTICE_MS);
    expect(useMarket.getState().rowNotices['1']).toBeUndefined();
    expect(useMarket.getState().kept).toEqual([]);
  });

  it('shows the refusal on the clicked row even when the board dropped it before the refusal came', async () => {
    const h = harness();
    useMarket.getState().openPanel('browse');
    await flush();
    useMarket.getState().buy(listing('1'));
    h.answer([listing('2', { unit: 5, total: 100 })]);
    await useMarket.getState().refreshBrowse();
    expect(useMarket.getState().kept).toEqual([]);
    useMarket.getState().refused(MarketReason.Sold, { kind: 'buy', listing: '1', unitPrice: 3 });
    expect(useMarket.getState().kept.map((l) => l.id)).toEqual(['1']);
    expect(useMarket.getState().rowNotices['1']).toBe('Sold to someone else first');
  });

  it('says a command the rate limit kept on its row', () => {
    const h = harness();
    h.refuse();
    useMarket.getState().cancel('5');
    expect(useMarket.getState().rowNotices['5']).toContain('Not sent');
  });

  it('lists the stack picked, at its market value, as the Sell form says', () => {
    const h = harness();
    const stack: InventoryItem = {
      id: '30',
      slot: 4,
      item: 'mat_hide',
      name: 'Hide',
      category: 'material',
      template: 8,
      mk: 2,
      count: 40,
    };
    useMarket.setState({
      prices: [
        {
          item: 'mat_hide',
          name: 'Hide',
          category: 'material',
          template: 8,
          mk: 2,
          value: 3,
          vendor: 560,
          listed: 0,
          lowest: 0,
          median: 0,
          min: 0,
          max: 0,
          sales: 0,
        },
      ],
    });
    useMarket.getState().setStacks([stack]);
    useMarket.getState().selectStack('30');
    expect(useMarket.getState().sell).toEqual({ stack: '30', quantity: 40, unitPrice: 3 });
    useMarket.getState().setSellQuantity(500);
    expect(useMarket.getState().sell.quantity).toBe(40);
    useMarket.getState().setSellQuantity(20);
    useMarket.getState().list();
    useMarket.getState().vendor();
    expect(h.sent).toEqual([{ kind: 'list', template: 8, mk: 2, quantity: 20, unitPrice: 3 }]);
    // The inventory moved (the units left): the Sell tab is free again, and the vendor can be asked.
    useMarket.getState().setStacks([{ ...stack, count: 20 }]);
    useMarket.getState().vendor();
    expect(h.sent[1]).toEqual({ kind: 'vendor', template: 8, mk: 2, quantity: 20 });
  });

  it('refuses to list a stack a server named no template for, without sending', () => {
    const h = harness();
    useMarket
      .getState()
      .setStacks([
        { id: '1', slot: 1, item: 'mat_hide', name: 'Hide', category: 'material', template: 0, mk: 1, count: 3 },
      ]);
    useMarket.getState().selectStack('1');
    useMarket.getState().list();
    expect(h.sent).toEqual([]);
    expect(useMarket.getState().rowNotices.sell).toContain('template');
  });

  it("announces a sale with its goods and tax, a purchase, and the vendor's payment", () => {
    harness();
    useMarket.getState().setMine({
      listed: [
        {
          ...listing('5'),
          fee: 1,
          listedTick: 100,
          expireTick: 1_728_100,
        },
      ],
      reserved: [],
      caps: { listings: 20, reserved: 20 },
    });
    useMarket
      .getState()
      .buy(listing('9', { name: 'Armor Core', mk: 3, qty: 1, unit: 900, total: 900, sellerName: 'Ann' }));
    const wallet = (transfers: unknown[]) => parseWallet({ open: true, credits: 1000, transfers })!;
    useWallet.getState().setWallet(wallet([]));
    useWallet.getState().setWallet(
      wallet([
        { seq: '4', tick: 10, kind: 'vendor', amount: 6, other: 'World' },
        { seq: '3', tick: 9, kind: 'sale', amount: -900, other: 'Ann' },
        { seq: '2', tick: 8, kind: 'tax', amount: -4, other: 'World' },
        { seq: '1', tick: 8, kind: 'sale', amount: 60, other: 'Bob' },
      ]),
    );
    expect(useChat.getState().lines.map((l) => l.text)).toEqual([
      'Sold Hide Mk II ×20 to Bob for 60 cr (tax 4)',
      'Bought Armor Core Mk III from Ann for 900 cr',
      'Vendor paid 6 cr',
    ]);
  });

  it('is opened on its tab by the terminal menu', () => {
    harness();
    useUi.getState().runContextAction({ kind: 'bazaar', tab: 'reserved' });
    expect(useMarket.getState().open).toBe(true);
    expect(useMarket.getState().tab).toBe('reserved');
  });
});

describe("the mock's desk", () => {
  /** A mock character with a few drops in its inventory, on a clock the test turns. */
  const mockDesk = () => {
    let now = 1000;
    const inventory = new MockInventory(7);
    inventory.revision(now);
    inventory.revision(now + MOCK_DROP_MS * 6);
    const desk = new MockMarket(7, inventory, () => now);
    return {
      inventory,
      desk,
      advance: (ms: number) => {
        now += ms;
      },
      json: async (url: string): Promise<unknown> => (await desk.fetch(url, { headers: {} })).json(),
    };
  };

  it('answers the four shapes, the board paged 20 at a time', async () => {
    const m = mockDesk();
    const page = parseBrowse(await m.json('swg/market?city=0&sort=price&page=1'));
    expect(page?.listings.length).toBe(20);
    expect(page!.pages).toBeGreaterThan(1);
    const units = page!.listings.map((l) => l.unit);
    expect([...units].sort((a, b) => a - b)).toEqual(units);
    expect(parseMine(await m.json('swg/market/mine'))?.listed).toEqual([]);
    expect(parsePrices(await m.json('swg/market/prices'))?.length).toBeGreaterThan(0);
    expect(parseWallet(await m.json('swg/wallet'))?.credits).toBe(MOCK_CREDITS);
  });

  it('sells one listing to the rival first: the player is refused 153, and it leaves the board', async () => {
    const m = mockDesk();
    const page = parseBrowse(await m.json(`swg/market?city=${String(MOCK_BAZAAR_REGION)}&sort=price&page=1`));
    const contested = page!.listings.find((l) => l.name === CONTESTED_NAME && l.unit === 50)!;
    expect(m.desk.apply({ kind: 'buy', listing: contested.id, unitPrice: contested.unit })).toBe(MarketReason.Sold);
    expect(m.desk.balance).toBe(MOCK_CREDITS);
    const again = parseBrowse(await m.json(`swg/market?city=${String(MOCK_BAZAAR_REGION)}&sort=price&page=1`));
    expect(again!.listings.some((l) => l.id === contested.id)).toBe(false);
  });

  it('lists, charges the fee, cancels back to the inventory here, and sells to its buyer elsewhere', async () => {
    const m = mockDesk();
    const stack = parseInventory(m.inventory.snapshot())!.items[0];
    const qty = Math.min(stack.count, 3);
    const before = m.inventory.held(stack.template, stack.mk);
    expect(m.desk.apply({ kind: 'list', template: stack.template, mk: stack.mk, quantity: qty, unitPrice: 100 })).toBe(
      0,
    );
    expect(m.inventory.held(stack.template, stack.mk)).toBe(before - qty);
    expect(m.desk.balance).toBe(MOCK_CREDITS - fees(qty * 100).listFee);
    let mine = parseMine(await m.json('swg/market/mine'))!;
    expect(mine.listed).toHaveLength(1);
    expect(m.desk.apply({ kind: 'cancel', listing: mine.listed[0].id })).toBe(0);
    expect(m.inventory.held(stack.template, stack.mk)).toBe(before);
    expect(m.desk.apply({ kind: 'cancel', listing: mine.listed[0].id })).toBe(MarketReason.NotYours);

    expect(m.desk.apply({ kind: 'list', template: stack.template, mk: stack.mk, quantity: qty, unitPrice: 100 })).toBe(
      0,
    );
    const paid = m.desk.balance;
    m.advance(MOCK_SALE_MS);
    mine = parseMine(await m.json('swg/market/mine'))!;
    expect(mine.listed).toEqual([]);
    expect(m.desk.balance).toBe(paid + fees(qty * 100).net);
  });

  it('buys here and delivers at once, buys elsewhere and reserves, collects only at its city', async () => {
    const m = mockDesk();
    const board = parseBrowse(await m.json('swg/market?city=0&sort=price&page=1'))!.listings;
    const here = board.find((l) => l.city === MOCK_BAZAAR_REGION && l.name !== CONTESTED_NAME && l.total <= 2000)!;
    const away = board.find((l) => l.city !== MOCK_BAZAAR_REGION && l.total <= 2000)!;
    const held = m.inventory.held(here.template, here.mk);
    expect(m.desk.apply({ kind: 'buy', listing: here.id, unitPrice: here.unit + 1 })).toBe(MarketReason.Sold);
    expect(m.desk.apply({ kind: 'buy', listing: here.id, unitPrice: here.unit })).toBe(0);
    expect(m.inventory.held(here.template, here.mk)).toBe(held + here.qty);
    expect(m.desk.apply({ kind: 'buy', listing: away.id, unitPrice: away.unit })).toBe(0);
    expect(m.desk.balance).toBe(MOCK_CREDITS - here.total - away.total);
    const mine = parseMine(await m.json('swg/market/mine'))!;
    expect(mine.reserved.map((r) => [r.id, r.state])).toEqual([[away.id, 'sold']]);
    expect(m.desk.apply({ kind: 'collect', listing: away.id })).toBe(MarketReason.WrongCity);
    expect(m.desk.apply({ kind: 'buy', listing: away.id, unitPrice: away.unit })).toBe(MarketReason.Sold);
  });

  it('pays the vendor the floor, and refuses what it will not buy or what is not held', () => {
    const m = mockDesk();
    const stack = m.inventory.snapshot().items[0];
    const held = m.inventory.held(stack.template, stack.mk);
    expect(held).toBeLessThan(99);
    expect(m.desk.apply({ kind: 'vendor', template: stack.template, mk: stack.mk, quantity: held + 1 })).toBe(
      MarketReason.NoItems,
    );
    // Over a stack's worth is malformed before it is short (§ 3.1's check order).
    expect(m.desk.apply({ kind: 'vendor', template: stack.template, mk: stack.mk, quantity: 100 })).toBe(
      MarketReason.BadListing,
    );
    expect(m.desk.apply({ kind: 'vendor', template: 99, mk: 1, quantity: 1 })).toBe(MarketReason.BadListing);
    const before = m.desk.balance;
    const reason = m.desk.apply({ kind: 'vendor', template: stack.template, mk: stack.mk, quantity: stack.count });
    // A material's few units may be worth less than a credit to the vendor: refused, nothing destroyed.
    if (reason === MarketReason.Worthless) {
      expect(m.inventory.held(stack.template, stack.mk)).toBe(stack.count);
    } else {
      expect(reason).toBe(0);
      expect(m.desk.balance).toBeGreaterThan(before);
    }
  });
});
