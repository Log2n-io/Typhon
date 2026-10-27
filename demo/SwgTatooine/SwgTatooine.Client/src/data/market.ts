import { CITIES } from './world-data';

/**
 * The bazaar as a client sees it (claude/design/SwgTatooine/design/bazaar.md § 5, § 6): the board browsed over HTTP from the
 * desk's snapshot, this character's listings and reservations (`/swg/market/mine`, read on its revisions), the price hints,
 * the five commands, and what their refusals read as.
 *
 * <b>The server computes, the client previews.</b> The fees and the vendor's payout are the desk's formulas
 * (`TatooineData.ListFeePerMille` …); the client repeats them only to show the seller what a listing will cost before it is
 * sent, and the server's answer — the `wallet` revision moving — is what the balance shows.
 */

/** A listing's quantity: one stack's worth (B2). */
export const MAX_LISTING_QTY = 99;

/** The dearest unit price a listing may ask: `TatooineData.MaxUnitPrice`. */
export const MAX_UNIT_PRICE = 1_000_000;

/** Per mille of a listing's total paid when it is listed, at least 1 cr: `TatooineData.ListFeePerMille` (B6). */
export const LIST_FEE_PER_MILLE = 10;

/** Per mille of a sale the seller pays the World: `TatooineData.SalesTaxPerMille` (B6). */
export const SALES_TAX_PER_MILLE = 80;

/** A browse page's rows: `TatooineData.BrowsePage`. */
export const BROWSE_PAGE = 20;

/** The caps the server answers `/mine` with, until it has: `TatooineData.MaxListings`, `MaxReserved` (B9). */
export const DEFAULT_CAPS: MarketCaps = { listings: 20, reserved: 20 };

/** How long a market command waits for a refusal, milliseconds: an accepted one is never answered (B11). */
export const MARKET_ANSWER_MS = 5000;

/**
 * Goods in a listing, as every market answer writes them (§ 5): the board's rows, `/mine`'s listed and reserved alike.
 */
export interface MarketGoods {
  /** The listing's raw entity id, as decimal text: 64 bits do not fit a JavaScript number. */
  readonly id: string;
  /** Its city: region = city index + 1, 1-based like every id (0 means none). */
  readonly city: number;
  /** The city's name as the server states it — twin realms share regions, so the region alone does not name it. */
  readonly cityName: string;
  /** The realm the city is in; -1 when the server did not say. */
  readonly realm: number;
  /** The item's asset id (`mat_hide`), which names its icon. */
  readonly item: string;
  readonly name: string;
  readonly category: string;
  /** The item template's id, what a list or a vendor sale names; 0 when the server did not say. */
  readonly template: number;
  readonly mk: number;
  readonly qty: number;
  /** Credits a unit. */
  readonly unit: number;
  /** `unit × qty`: what a buyer pays. */
  readonly total: number;
  /** Seconds since it was listed; 0 when the server did not say. */
  readonly ageS: number;
}

/** One listing on the board, as `GET /swg/market` answers it. */
export interface MarketListing extends MarketGoods {
  /** The seller's character id, as decimal text. */
  readonly seller: string;
  readonly sellerName: string;
}

/** One page of the board. */
export interface BrowsePage {
  /** The snapshot's version: the same version is the same board. */
  readonly v: number;
  /** The city browsed, 0 for all. */
  readonly city: number;
  /** Listings matching, all pages. */
  readonly total: number;
  /** 1-based. */
  readonly page: number;
  readonly pages: number;
  readonly listings: readonly MarketListing[];
}

/** One of this character's listings still on the board (`/mine`'s `listed`). */
export interface MineListing extends MarketGoods {
  /** The list fee it paid. */
  readonly fee: number;
  /** World ticks: when it was listed, and when it is returned unsold. */
  readonly listedTick: number;
  readonly expireTick: number;
}

/** Goods waiting for this character at a city: bought there (`sold`), or its own listing back (`returned`). */
export interface Reservation extends MarketGoods {
  readonly state: 'sold' | 'returned';
  readonly closedTick: number;
}

export interface MarketCaps {
  readonly listings: number;
  readonly reserved: number;
}

/** `GET /swg/market/mine`. */
export interface MarketMine {
  readonly listed: readonly MineListing[];
  readonly reserved: readonly Reservation[];
  readonly caps: MarketCaps;
}

/** One (item, Mk) of `GET /swg/market/prices`: the Sell tab's hints. */
export interface MarketPrice {
  readonly item: string;
  readonly name: string;
  readonly category: string;
  readonly template: number;
  readonly mk: number;
  /** The market value of a unit, credits — decimal (a material's 1.7); the others below are whole credits. */
  readonly value: number;
  /** What the vendor pays a unit, milli-credits (credits × 1000). */
  readonly vendor: number;
  /** Units listed now. */
  readonly listed: number;
  /** The cheapest unit price listed now, 0 for none. */
  readonly lowest: number;
  /** The median unit price of the last sales, 0 for none; their cheapest and dearest. */
  readonly median: number;
  readonly min: number;
  readonly max: number;
  readonly sales: number;
}

/** The categories the board filters by. */
export type MarketCategory = '' | 'material' | 'core' | 'addon';

/** The board's orders: cheapest first, dearest first, newest first, highest Mk first. */
export type MarketSort = 'price' | '-price' | 'new' | 'mk';

/** What the Browse tab asks for. */
export interface MarketQuery {
  /** A city's region, 0 for every city. */
  readonly city: number;
  /**
   * The realm of the city browsed, -1 for the server's default — a city named without its realm is planet 0's, and twin realms
   * share regions.
   */
  readonly realm: number;
  readonly cat: MarketCategory;
  /** An asset id, `''` for any. */
  readonly item: string;
  /** 1-5, 0 for any. */
  readonly mk: number;
  /** The dearest unit price shown, 0 for no limit. */
  readonly max: number;
  readonly sort: MarketSort;
  /** 1-based. */
  readonly page: number;
}

export const DEFAULT_QUERY: MarketQuery = {
  city: 0,
  realm: -1,
  cat: '',
  item: '',
  mk: 0,
  max: 0,
  sort: 'price',
  page: 1,
};

/** The URL of a board page, relative to the page as the other endpoints are; only the filters that are set. */
export function browseUrl(q: MarketQuery): string {
  const p = new URLSearchParams();
  p.set('city', String(q.city));
  if (q.city > 0 && q.realm >= 0) {
    p.set('realm', String(q.realm));
  }

  if (q.cat !== '') {
    p.set('cat', q.cat);
  }

  if (q.item !== '') {
    p.set('item', q.item);
  }

  if (q.mk > 0) {
    p.set('mk', String(q.mk));
  }

  if (q.max > 0) {
    p.set('max', String(q.max));
  }

  p.set('sort', q.sort);
  p.set('page', String(Math.max(1, q.page)));
  return `swg/market?${p.toString()}`;
}

/** The name of city `region` (index + 1), or `''` for none. */
export function cityName(region: number): string {
  return CITIES[region - 1]?.name ?? '';
}

/** The region (city index + 1) of the city whose centre is nearest (`x`, `z`): where a bazaar terminal there lists. */
export function regionAt(x: number, z: number): number {
  let best = 0;
  let bestD = Infinity;
  CITIES.forEach((c, i) => {
    const d = (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z);
    if (d < bestD) {
      bestD = d;
      best = i + 1;
    }
  });
  return best;
}

/** What a listing of `total` credits costs and yields (B6): the fee now, the tax at the sale, what the seller is left with. */
export function fees(total: number): { readonly listFee: number; readonly tax: number; readonly net: number } {
  const t = Math.max(0, Math.floor(total));
  const tax = Math.floor((t * SALES_TAX_PER_MILLE) / 1000);
  return { listFee: Math.max(1, Math.floor((t * LIST_FEE_PER_MILLE) / 1000)), tax, net: t - tax };
}

/** The tax a sale of `total` pays, which is what the seller's wallet shows beside the sale. */
export function salesTax(total: number): number {
  return fees(total).tax;
}

/**
 * What the vendor pays for `count` units (B7): ⌊count × value × 20 %⌋, from the per-unit milli-credits `/prices` states.
 * 0 is a refusal (`MarketWorthless`).
 */
export function vendorPayout(count: number, vendorMilli: number): number {
  return Math.floor((Math.max(0, count) * Math.max(0, vendorMilli)) / 1000);
}

/** The fee preview the Sell tab shows: "fee 9 cr now · tax 72 cr at sale · you get 819". */
export function feeLine(total: number): string {
  const f = fees(total);
  return `fee ${f.listFee.toLocaleString('en-US')} cr now · tax ${f.tax.toLocaleString('en-US')} cr at sale · you get ${f.net.toLocaleString('en-US')}`;
}

/** A listing's goods as a line says them: `Hide Mk II ×20`, `Armor Core Mk III`. */
export function itemWhat(name: string, mk: number, qty: number): string {
  const numeral = ['', 'I', 'II', 'III', 'IV', 'V'][mk] ?? '';
  return `${name}${numeral === '' ? '' : ` Mk ${numeral}`}${qty > 1 ? ` ×${String(qty)}` : ''}`;
}

/**
 * The chat line of a wallet row the bazaar wrote (§ 6, `wallet-store`), or `null` for a kind it does not announce: a sale
 * from either side and a vendor's payment. The list fee and the tax are said with what they belong to, not alone.
 *
 * @param amount Signed from this wallet's side: a sale received is positive, a purchase negative.
 * @param other The other side's name.
 * @param tax The tax the same sale paid (a `tax` row of the same tick), 0 for none.
 * @param what The goods (`itemWhat`), or `null` when this client does not know them.
 */
export function marketWalletLine(
  kind: string,
  amount: number,
  other: string | null,
  tax: number,
  what: string | null,
): string | null {
  const cr = (n: number): string => `${Math.abs(n).toLocaleString('en-US')} cr`;
  const who = other ?? 'somebody';
  switch (kind) {
    case 'sale':
      if (amount > 0) {
        return `Sold ${what === null ? '' : `${what} `}to ${who} for ${cr(amount)} (tax ${String(tax)})`;
      }

      return `Bought ${what === null ? '' : `${what} `}from ${who} for ${cr(amount)}`;
    case 'vendor':
      return `Vendor paid ${cr(amount)}`;
    default:
      return null;
  }
}

/** An age as the board writes it: `45 s`, `12 min`, `3 h`. */
export function formatAge(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  if (s < 60) {
    return `${String(s)} s`;
  }

  return s < 3600 ? `${String(Math.floor(s / 60))} min` : `${String(Math.floor(s / 3600))} h`;
}

/** The refusals of the five commands, as the server's ACKS reasons name them (`TatooineReplication.MarketNotAtBazaar` …). */
export const MARKET_REASONS: Readonly<Record<number, string>> = {
  142: 'You are not in the world',
  151: 'Stand at a bazaar terminal to do that',
  152: 'Collect it at its city',
  153: 'Sold to someone else first',
  154: 'Not enough credits',
  155: 'You do not have those items',
  156: 'That cannot be listed',
  157: 'You already have the most listings allowed',
  158: 'You already have the most purchases waiting — collect some first',
  159: 'No room in your inventory',
  160: 'That listing is not yours',
  161: 'You cannot buy your own listing',
  162: 'The server could not apply it — nothing changed, try again',
  163: 'The vendor will not buy that',
};

/** The reason codes by name, the order § 3.1 gives them. */
export const MarketReason = {
  NotInWorld: 142,
  NotAtBazaar: 151,
  WrongCity: 152,
  Sold: 153,
  NotEnough: 154,
  NoItems: 155,
  BadListing: 156,
  ListingCap: 157,
  ReservedCap: 158,
  NoRoom: 159,
  NotYours: 160,
  OwnListing: 161,
  Failed: 162,
  Worthless: 163,
} as const;

/**
 * What a refusal reads as; a reason this table does not know still says something. A wrong city names the city when the
 * caller knows it: "Collect it at Bestine".
 */
export function marketRefusal(reason: number, city = 0): string {
  if (reason === MarketReason.WrongCity && cityName(city) !== '') {
    return `Collect it at ${cityName(city)}`;
  }

  return MARKET_REASONS[reason] ?? `Refused (${String(reason)})`;
}

/** The five commands (§ 3.1), as plain data. */
export type MarketCommand =
  | {
      readonly kind: 'list';
      readonly template: number;
      readonly mk: number;
      readonly quantity: number;
      readonly unitPrice: number;
    }
  | { readonly kind: 'cancel'; readonly listing: string }
  | { readonly kind: 'buy'; readonly listing: string; readonly unitPrice: number }
  /** `listing` `'0'` collects everything of this character's that this city holds, oldest first. */
  | { readonly kind: 'collect'; readonly listing: string }
  | { readonly kind: 'vendor'; readonly template: number; readonly mk: number; readonly quantity: number };

/** The command's name in the server's catalog, and its fields as the codec names them. `null` for a malformed one. */
export function marketWire(
  command: MarketCommand,
): { readonly name: string; readonly values: Readonly<Record<string, number | bigint>> } | null {
  switch (command.kind) {
    case 'list':
      return {
        name: 'MarketList',
        values: {
          template: command.template,
          mk: command.mk,
          quantity: command.quantity,
          unitPrice: command.unitPrice,
        },
      };
    case 'vendor':
      return {
        name: 'SellToVendor',
        values: { template: command.template, mk: command.mk, quantity: command.quantity },
      };
    case 'cancel':
    case 'collect': {
      const listing = idOf(command.listing);
      return listing === null
        ? null
        : { name: command.kind === 'cancel' ? 'MarketCancel' : 'MarketCollect', values: { listing } };
    }

    case 'buy': {
      const listing = idOf(command.listing);
      return listing === null ? null : { name: 'MarketBuy', values: { listing, unitPrice: command.unitPrice } };
    }
  }
}

/** The row a command is about — where its refusal is shown: the listing, `'all'` for a collect-all, `'sell'` for the Sell tab. */
export function commandRow(command: MarketCommand): string {
  switch (command.kind) {
    case 'list':
    case 'vendor':
      return 'sell';
    case 'collect':
      return command.listing === '0' ? 'all' : command.listing;
    default:
      return command.listing;
  }
}

/** A 64-bit id written as decimal text, as a bigint; `null` for anything else. */
function idOf(text: string): bigint | null {
  return /^\d+$/.test(text) ? BigInt(text) : null;
}

/** The bazaar panel's four tabs (§ 6). */
export type BazaarTab = 'browse' | 'sell' | 'mine' | 'reserved';

/** `GET /swg/market` checked; `null` for anything else. A malformed row is dropped, not the answer. */
export function parseBrowse(answer: unknown): BrowsePage | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const a = answer as Record<string, unknown>;
  if (!Array.isArray(a.listings) || typeof a.total !== 'number') {
    return null;
  }

  const listings: MarketListing[] = [];
  for (const raw of a.listings) {
    const l = listingOf(raw);
    if (l !== null) {
      listings.push(l);
    }
  }

  return {
    v: num(a.v),
    city: num(a.city),
    total: a.total,
    page: Math.max(1, num(a.page)),
    pages: Math.max(1, num(a.pages)),
    listings,
  };
}

/** `GET /swg/market/mine` checked; `null` for anything else. */
export function parseMine(answer: unknown): MarketMine | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const a = answer as Record<string, unknown>;
  if (!Array.isArray(a.listed) || !Array.isArray(a.reserved)) {
    return null;
  }

  const listed: MineListing[] = [];
  for (const raw of a.listed) {
    const r = record(raw);
    const goods = r === null ? null : goodsOf(r);
    if (r !== null && goods !== null) {
      listed.push({ ...goods, fee: num(r.fee), listedTick: num(r.listedTick), expireTick: num(r.expireTick) });
    }
  }

  const reserved: Reservation[] = [];
  for (const raw of a.reserved) {
    const r = record(raw);
    const goods = r === null ? null : goodsOf(r);
    if (r !== null && goods !== null && (r.state === 'sold' || r.state === 'returned')) {
      reserved.push({ ...goods, state: r.state, closedTick: num(r.closedTick) });
    }
  }

  const caps = record(a.caps);
  return {
    listed,
    reserved,
    caps: {
      listings: typeof caps?.listings === 'number' ? caps.listings : DEFAULT_CAPS.listings,
      reserved: typeof caps?.reserved === 'number' ? caps.reserved : DEFAULT_CAPS.reserved,
    },
  };
}

/** `GET /swg/market/prices` (`{v, prices: […]}`) checked: its rows, `null` for anything else. A bare array is read too. */
export function parsePrices(answer: unknown): MarketPrice[] | null {
  const rows = Array.isArray(answer) ? answer : record(answer)?.prices;
  if (!Array.isArray(rows)) {
    return null;
  }

  const out: MarketPrice[] = [];
  for (const raw of rows) {
    const r = record(raw);
    if (r === null || typeof r.item !== 'string' || typeof r.value !== 'number') {
      continue;
    }

    out.push({
      item: r.item,
      name: str(r.name),
      category: str(r.category),
      template: num(r.template),
      mk: num(r.mk),
      value: r.value,
      vendor: num(r.vendor),
      listed: num(r.listed),
      lowest: num(r.lowest),
      median: num(r.median),
      min: num(r.min),
      max: num(r.max),
      sales: num(r.sales),
    });
  }

  return out;
}

/** The price row of (`item`, `mk`), or `undefined`. */
export function priceOf(prices: readonly MarketPrice[], item: string, mk: number): MarketPrice | undefined {
  return prices.find((p) => p.item === item && p.mk === mk);
}

function listingOf(raw: unknown): MarketListing | null {
  const r = record(raw);
  const goods = r === null ? null : goodsOf(r);
  return r === null || goods === null
    ? null
    : { ...goods, seller: idText(r.seller) ?? '', sellerName: str(r.sellerName) };
}

/** The goods every market row carries; `null` for a row without an id, a quantity or a price. */
function goodsOf(r: Record<string, unknown>): MarketGoods | null {
  const id = idText(r.id);
  if (id === null || typeof r.qty !== 'number' || typeof r.unit !== 'number') {
    return null;
  }

  const city = num(r.city);
  return {
    id,
    city,
    cityName: typeof r.cityName === 'string' && r.cityName !== '' ? r.cityName : cityName(city),
    realm: typeof r.realm === 'number' ? r.realm : -1,
    item: str(r.item),
    name: str(r.name),
    category: str(r.category),
    template: num(r.template),
    mk: num(r.mk),
    qty: r.qty,
    unit: r.unit,
    total: typeof r.total === 'number' ? r.total : r.unit * r.qty,
    ageS: num(r.ageS),
  };
}

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;
}

/** An id the server may write as a JSON string (64 bits) or, below 2^53, a number: decimal text either way. */
function idText(value: unknown): string | null {
  if (typeof value === 'string' && /^\d+$/.test(value)) {
    return value;
  }

  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? String(value) : null;
}

function num(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : '';
}
