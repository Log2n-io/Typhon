import {
  BROWSE_PAGE,
  DEFAULT_CAPS,
  fees,
  MarketReason,
  MAX_LISTING_QTY,
  MAX_UNIT_PRICE,
  vendorPayout,
  type MarketCommand,
} from '../market';
import type { RevisionFetch } from '../revision-reader';
import type { WalletTransfer } from '../wallet';
import { CITIES } from '../world-data';
import { mockItem, MOCK_MATERIALS, MOCK_PARTS, type MockInventory, type MockItemDef } from './inventory';
import { Rng } from './rng';

/**
 * The mock's bazaar (bazaar.md § 6, "`data/mock/`"): the four endpoints answered and the five commands applied by the desk's
 * rules (§ 3.1, in its check order), so the panel can be exercised without a server. The mock's character stands at a
 * bazaar of {@link MOCK_BAZAAR_REGION}, has a wallet of its own (`GET /swg/wallet`, revision moving with each transfer), and
 * trades with the inventory the mock already loots into.
 *
 * <b>Two scripted others.</b> A rival buys the {@link CONTESTED_NAME} listing in the same tick the player does and wins it,
 * so a click on it is refused `MarketSold` — the race loser's line. And a buyer elsewhere takes each of the player's listings
 * {@link MOCK_SALE_MS} after it is listed, so the seller's side is seen: the wallet up by the price less the tax, the line in
 * the chat.
 */

/** The city the mock's character trades at: Port Sable, the first. */
export const MOCK_BAZAAR_REGION = 1;

/** How long a listing of the player's waits for the mock's buyer, milliseconds. */
export const MOCK_SALE_MS = 25_000;

/** The listing a rival always wins: a cheap Armor Core at Port Sable. */
export const CONTESTED_NAME = 'Armor Core';

/** The mock character's starting credits. */
export const MOCK_CREDITS = 5000;

/** Milli-credits a unit, by Mk I-V (`balance_model.py` `mat_value`, `core_value`; the cores' middle Mks interpolated here). */
const MATERIAL_VALUE = [1700, 2800, 4500, 7200, 11_400];
const CORE_VALUE = [726_000, 1_435_000, 2_838_000, 5_612_000, 11_106_000];

const SELLERS = ['Kessa Dorn', 'Tobin Vax', 'Ree Valo', 'Jask', 'Mira Sunn', 'Old Pell', 'Dax Rook', 'Naro'];
const RIVAL = 'Vex Talon';
const BUYER = 'Ilsa Krane';
const SELF_ID = '900001';
const LISTED = 1;
const SOLD = 2;
const RETURNED = 3;
const TICK_MS = 50;

interface Listing {
  readonly id: string;
  readonly def: MockItemDef;
  readonly mk: number;
  readonly qty: number;
  readonly unit: number;
  readonly city: number;
  readonly seller: string;
  readonly sellerName: string;
  readonly listedMs: number;
  state: number;
  collector: string;
  closedTick: number;
  contested: boolean;
}

/** The market value of (`def`, `mk`), milli-credits a unit; 0 for what has none. */
export function mockValue(def: MockItemDef, mk: number): number {
  const table = def.category === 'material' ? MATERIAL_VALUE : CORE_VALUE;
  return table[mk - 1] ?? 0;
}

export class MockMarket {
  private readonly inventory: MockInventory;
  private readonly rng: Rng;
  private readonly now: () => number;
  private readonly listings = new Map<string, Listing>();
  private readonly transfers: WalletTransfer[] = [];
  private credits = 0;
  private walletRev = 1;
  private nextId = 7_000_001;
  private nextSeq = 1;
  private version = 1;

  /**
   * @param seed The mock world's seed: the same seed lists the same board.
   * @param inventory The mock character's inventory, which listings take from and collects give to.
   * @param now A monotonic clock in milliseconds: a listing's age, the mock buyer's delay.
   */
  constructor(seed: number, inventory: MockInventory, now: () => number = () => performance.now()) {
    this.inventory = inventory;
    this.rng = new Rng(seed ^ 0x5a1e);
    this.now = now;
    this.transfer('grant', MOCK_CREDITS, 'World');
    this.seedBoard();
  }

  /** The wallet's revision, the mock buyer acting on whatever came due first. */
  walletRevision(): number {
    this.settle();
    return this.walletRev;
  }

  /** The mock character's balance. */
  get balance(): number {
    return this.credits;
  }

  /**
   * Applies one command as the desk would (§ 3.1, its refusals in their check order): 0 when applied, otherwise the reason it
   * was refused, having written nothing.
   */
  apply(command: MarketCommand): number {
    this.settle();
    switch (command.kind) {
      case 'list':
        return this.list(command.template, command.mk, command.quantity, command.unitPrice);
      case 'cancel':
        return this.cancel(command.listing);
      case 'buy':
        return this.buy(command.listing, command.unitPrice);
      case 'collect':
        return this.collect(command.listing);
      case 'vendor':
        return this.vendor(command.template, command.mk, command.quantity);
    }
  }

  /** A `fetch` for the market's and the wallet's readers: the four endpoints of § 5 and `swg/wallet`, anything else 404. */
  readonly fetch: RevisionFetch = (url) => {
    this.settle();
    const [path, search = ''] = url.split('?');
    let answer: unknown = null;
    switch (path) {
      case 'swg/market':
        answer = this.browse(new URLSearchParams(search));
        break;
      case 'swg/market/mine':
        answer = this.mine();
        break;
      case 'swg/market/prices':
        answer = { prices: this.prices() };
        break;
      case 'swg/wallet':
        answer = { open: true, credits: this.credits, transfers: [...this.transfers] };
        break;
    }

    return Promise.resolve({
      ok: answer !== null,
      status: answer === null ? 404 : 200,
      json: () => Promise.resolve(answer),
    });
  };

  private list(template: number, mk: number, quantity: number, unitPrice: number): number {
    const def = mockItem(template);
    if (
      def === undefined ||
      mockValue(def, mk) === 0 ||
      quantity < 1 ||
      quantity > MAX_LISTING_QTY ||
      unitPrice < 1 ||
      unitPrice > MAX_UNIT_PRICE
    ) {
      return MarketReason.BadListing;
    }

    if (this.inventory.held(template, mk) < quantity) {
      return MarketReason.NoItems;
    }

    if (this.mineIn(LISTED, SELF_ID).length >= DEFAULT_CAPS.listings) {
      return MarketReason.ListingCap;
    }

    const fee = fees(quantity * unitPrice).listFee;
    if (fee > this.credits) {
      return MarketReason.NotEnough;
    }

    const id = String(this.nextId++);
    this.inventory.take(template, mk, quantity, 'list', this.tick(), id);
    this.add(
      { def, mk, qty: quantity, unit: unitPrice, city: MOCK_BAZAAR_REGION, seller: SELF_ID, sellerName: 'You' },
      id,
    );
    this.transfer('listfee', -fee, 'World');
    return 0;
  }

  private cancel(id: string): number {
    const l = this.listings.get(id);
    if (l?.seller !== SELF_ID) {
      return MarketReason.NotYours;
    }

    if (l.state !== LISTED) {
      return MarketReason.Sold;
    }

    this.close(l, RETURNED, SELF_ID);
    this.deliverIfHere(l, 'return');
    return 0;
  }

  private buy(id: string, unitPrice: number): number {
    const l = this.listings.get(id);
    if (l?.state !== LISTED || l.unit !== unitPrice) {
      return MarketReason.Sold;
    }

    if (l.contested) {
      // The rival's buy arrived first in this tick: the listing is theirs, and this one is the race's loser.
      this.close(l, SOLD, '900999');
      this.listings.delete(l.id);
      return MarketReason.Sold;
    }

    if (l.seller === SELF_ID) {
      return MarketReason.OwnListing;
    }

    if (this.mineIn(SOLD, SELF_ID).length >= DEFAULT_CAPS.reserved) {
      return MarketReason.ReservedCap;
    }

    const total = l.unit * l.qty;
    if (total > this.credits) {
      return MarketReason.NotEnough;
    }

    this.transfer('sale', -total, l.sellerName);
    this.close(l, SOLD, SELF_ID);
    this.deliverIfHere(l, 'buy');
    return 0;
  }

  private collect(id: string): number {
    if (id === '0') {
      const mine = [...this.listings.values()]
        .filter((l) => l.collector === SELF_ID && l.state !== LISTED && l.city === MOCK_BAZAAR_REGION)
        .sort((a, b) => a.closedTick - b.closedTick);
      let any = false;
      for (const l of mine) {
        any = this.deliverIfHere(l, l.state === SOLD ? 'buy' : 'return') || any;
      }

      return any || mine.length === 0 ? 0 : MarketReason.NoRoom;
    }

    const l = this.listings.get(id);
    if (l === undefined || l.state === LISTED || l.collector !== SELF_ID) {
      return MarketReason.NotYours;
    }

    if (l.city !== MOCK_BAZAAR_REGION) {
      return MarketReason.WrongCity;
    }

    return this.deliverIfHere(l, l.state === SOLD ? 'buy' : 'return') ? 0 : MarketReason.NoRoom;
  }

  private vendor(template: number, mk: number, quantity: number): number {
    const def = mockItem(template);
    if (def === undefined || quantity < 1 || quantity > MAX_LISTING_QTY) {
      return MarketReason.BadListing;
    }

    if (this.inventory.held(template, mk) < quantity) {
      return MarketReason.NoItems;
    }

    const paid = vendorPayout(quantity, vendorMilli(def, mk));
    if (paid === 0) {
      return MarketReason.Worthless;
    }

    this.inventory.take(template, mk, quantity, 'vendor', this.tick(), String(this.nextSeq));
    this.transfer('vendor', paid, 'World');
    return 0;
  }

  /** A closed listing of this city goes into the collector's inventory if it fits, and is destroyed (B4); false if it does not fit. */
  private deliverIfHere(l: Listing, kind: 'buy' | 'return'): boolean {
    if (l.city !== MOCK_BAZAAR_REGION || !this.inventory.give(l.def, l.mk, l.qty, kind, this.tick(), l.id)) {
      return false;
    }

    this.listings.delete(l.id);
    this.version++;
    return true;
  }

  private close(l: Listing, state: number, collector: string): void {
    l.state = state;
    l.collector = collector;
    l.closedTick = this.tick();
    this.version++;
  }

  /** The mock's buyer elsewhere: each of the player's listings older than {@link MOCK_SALE_MS} is bought, the seller paid less the tax. */
  private settle(): void {
    const now = this.now();
    for (const l of this.listings.values()) {
      if (l.seller !== SELF_ID || l.state !== LISTED || now - l.listedMs < MOCK_SALE_MS) {
        continue;
      }

      const total = l.unit * l.qty;
      this.close(l, SOLD, '900777');
      // Reserved for the buyer at its city: gone from the seller's view either way.
      this.listings.delete(l.id);
      this.transfer('sale', total, BUYER);
      const tax = fees(total).tax;
      if (tax > 0) {
        this.transfer('tax', -tax, 'World');
      }
    }
  }

  private browse(q: URLSearchParams): unknown {
    const city = Number(q.get('city') ?? '0');
    const cat = q.get('cat') ?? '';
    const item = q.get('item') ?? '';
    const mk = Number(q.get('mk') ?? '0');
    const max = Number(q.get('max') ?? '0');
    const sort = q.get('sort') ?? 'price';
    const rows = [...this.listings.values()].filter(
      (l) =>
        l.state === LISTED &&
        (city === 0 || l.city === city) &&
        (cat === '' || l.def.category === cat) &&
        (item === '' || l.def.item === item) &&
        (mk === 0 || l.mk === mk) &&
        (max === 0 || l.unit <= max),
    );
    rows.sort((a, b) => {
      switch (sort) {
        case '-price':
          return b.unit - a.unit || a.listedMs - b.listedMs;
        case 'new':
          return b.listedMs - a.listedMs;
        case 'mk':
          return b.mk - a.mk || a.unit - b.unit;
        default:
          return a.unit - b.unit || a.listedMs - b.listedMs;
      }
    });
    const pages = Math.max(1, Math.ceil(rows.length / BROWSE_PAGE));
    const page = Math.min(pages, Math.max(1, Number(q.get('page') ?? '1')));
    const now = this.now();
    return {
      v: this.version,
      city,
      total: rows.length,
      page,
      pages,
      listings: rows.slice((page - 1) * BROWSE_PAGE, page * BROWSE_PAGE).map((l) => ({
        id: l.id,
        city: l.city,
        item: l.def.item,
        name: l.def.name,
        category: l.def.category,
        template: l.def.template,
        mk: l.mk,
        qty: l.qty,
        unit: l.unit,
        total: l.unit * l.qty,
        seller: l.seller,
        sellerName: l.sellerName,
        ageS: Math.floor((now - l.listedMs) / 1000),
      })),
    };
  }

  private mine(): unknown {
    const now = this.now();
    return {
      listed: this.mineIn(LISTED, SELF_ID).map((l) => ({
        id: l.id,
        city: l.city,
        item: l.def.item,
        name: l.def.name,
        template: l.def.template,
        mk: l.mk,
        qty: l.qty,
        unit: l.unit,
        total: l.unit * l.qty,
        ageS: Math.floor((now - l.listedMs) / 1000),
      })),
      reserved: [...this.listings.values()]
        .filter((l) => l.state !== LISTED && l.collector === SELF_ID)
        .map((l) => ({
          id: l.id,
          city: l.city,
          item: l.def.item,
          name: l.def.name,
          mk: l.mk,
          qty: l.qty,
          unit: l.unit,
          state: l.state === SOLD ? 'sold' : 'returned',
          closedTick: l.closedTick,
        })),
      caps: DEFAULT_CAPS,
    };
  }

  private prices(): unknown[] {
    const out: unknown[] = [];
    for (const def of [...MOCK_MATERIALS, ...MOCK_PARTS]) {
      for (let mk = 1; mk <= 5; mk++) {
        const units = [...this.listings.values()].filter((l) => l.state === LISTED && l.def === def && l.mk === mk);
        const value = Math.floor(mockValue(def, mk) / 1000);
        out.push({
          item: def.item,
          template: def.template,
          mk,
          value,
          vendor: vendorMilli(def, mk),
          listed: units.reduce((n, l) => n + l.qty, 0),
          lowest: units.length === 0 ? 0 : Math.min(...units.map((l) => l.unit)),
          median: units.length === 0 ? 0 : Math.max(1, value),
          sales: 0,
        });
      }
    }

    return out;
  }

  private mineIn(state: number, who: string): Listing[] {
    return [...this.listings.values()].filter((l) =>
      state === LISTED ? l.state === LISTED && l.seller === who : l.state === state && l.collector === who,
    );
  }

  private seedBoard(): void {
    const now = this.now();
    for (let i = 0; i < 48; i++) {
      const part = this.rng.next() < 0.35;
      const def = part
        ? MOCK_PARTS[this.rng.int(0, MOCK_PARTS.length)]
        : MOCK_MATERIALS[this.rng.int(0, MOCK_MATERIALS.length)];
      const mk = 1 + Math.min(3, Math.floor(this.rng.next() * this.rng.next() * 5));
      const value = Math.max(1, mockValue(def, mk) / 1000);
      this.add(
        {
          def,
          mk,
          qty: part ? 1 : this.rng.int(5, 100),
          unit: Math.max(1, Math.round(value * this.rng.range(0.85, 1.4))),
          city: 1 + this.rng.int(0, CITIES.length),
          seller: String(800_000 + i),
          sellerName: SELLERS[this.rng.int(0, SELLERS.length)],
        },
        String(this.nextId++),
        now - this.rng.int(10, 20_000) * 1000,
      );
    }

    // The race: the cheapest core at the mock's own city, which the rival always buys first.
    const armor = MOCK_PARTS.find((p) => p.name === CONTESTED_NAME) ?? MOCK_PARTS[0];
    const id = String(this.nextId++);
    this.add(
      { def: armor, mk: 2, qty: 1, unit: 50, city: MOCK_BAZAAR_REGION, seller: '800100', sellerName: RIVAL },
      id,
      now - 60_000,
    );
    const contested = this.listings.get(id);
    if (contested !== undefined) {
      contested.contested = true;
    }
  }

  private add(
    l: Pick<Listing, 'def' | 'mk' | 'qty' | 'unit' | 'city' | 'seller' | 'sellerName'>,
    id: string,
    listedMs = this.now(),
  ): void {
    this.listings.set(id, { ...l, id, listedMs, state: LISTED, collector: '', closedTick: 0, contested: false });
    this.version++;
  }

  private transfer(kind: string, amount: number, other: string): void {
    this.credits += amount;
    this.transfers.unshift({ seq: String(this.nextSeq++), tick: this.tick(), kind, amount, other });
    this.transfers.length = Math.min(this.transfers.length, 20);
    this.walletRev++;
  }

  private tick(): number {
    return Math.floor(this.now() / TICK_MS);
  }
}

/** What the vendor pays a unit, milli-credits: 20 % of its value (`TatooineData.VendorBuysPerMille`). */
function vendorMilli(def: MockItemDef, mk: number): number {
  return Math.floor((mockValue(def, mk) * 200) / 1000);
}
