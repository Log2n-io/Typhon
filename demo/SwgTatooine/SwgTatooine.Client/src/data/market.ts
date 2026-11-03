import type { ItemStats } from './items';
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

/**
 * What the vendor pays for one item with stats (Loïc, 2026-11-03): its payout by the condition it has left, ⌊payout × condition / max⌋ — the server's
 * `VendorFromCommand`. 0 means it is scrapped: taken for nothing, broken or worn too far to be worth a credit. An item without a condition (a part) is paid
 * whole.
 */
export function itemVendorPayout(
  vendorMilli: number,
  stats: { readonly condition: number; readonly maxCondition: number; readonly broken?: boolean },
  count = 1,
): number {
  // A crate (crafting-catalog.md § 11.2) is sold whole: its count's payout, its items all pristine.
  const full = vendorPayout(count, vendorMilli);
  if (stats.maxCondition <= 0) {
    return full;
  }

  return stats.broken === true ? 0 : Math.floor((full * Math.max(0, stats.condition)) / stats.maxCondition);
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
  164: 'That cannot be worn, or is not yours',
  165: 'Your combat level is too low for that Mk',
  166: 'No room in your inventory to take it off',
  167: 'The server could not apply it — nothing changed, try again',
  168: 'Equip a weapon for that stance first',
  169: 'Stand at a crafting station to start a craft',
  170: 'Finish the craft under way first',
  171: 'No craft under way',
  172: 'Those inputs are not in your inventory',
  173: 'No point left, or no such line',
  174: 'No room in your inventory for the result',
  175: 'Not enough credits for the fee',
  176: 'The server could not apply it — nothing changed, try again',
  177: 'This craft already has its add-on, or takes none',
  178: 'That item cannot be repaired',
  179: 'You need a repair kit of its kind',
  180: 'It has had its two repairs',
  181: 'Only a crafted item in your inventory can be salvaged',
  182: 'Not worn down enough to repair: it would lower its condition',
  184: 'That does not refine, or not that many at once',
  185: 'Those cannot be packed together: identical, unused, ten to a crate',
  186: 'Nothing to unpack, or no room for it',
  187: 'Unpack it first',
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
  EquipNotYours: 164,
  EquipLevel: 165,
  EquipNoRoom: 166,
  EquipFailed: 167,
  StanceNoWeapon: 168,
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
      /** An item with stats, listed by its id (`InventoryItem.id`), quantity 1; absent for units. */
      readonly item?: string;
    }
  | { readonly kind: 'cancel'; readonly listing: string }
  | { readonly kind: 'buy'; readonly listing: string; readonly unitPrice: number }
  /** `listing` `'0'` collects everything of this character's that this city holds, oldest first. */
  | { readonly kind: 'collect'; readonly listing: string }
  | {
      readonly kind: 'vendor';
      readonly template: number;
      readonly mk: number;
      readonly quantity: number;
      readonly item?: string;
    }
  /** Standard issue from the vendor (crafting-catalog.md § 15.3), at the price it was shown; a material by the quantity (1-99), one otherwise. */
  | { readonly kind: 'standard'; readonly template: number; readonly price: number; readonly quantity?: number }
  /** Wear an item with stats (crafting-build.md § 2.2), by its id. */
  | { readonly kind: 'equip'; readonly item: string }
  /** Take off what is worn in an equip slot, 101-107. */
  | { readonly kind: 'unequip'; readonly slot: number }
  /** Start a craft (crafting-build.md C2): the Mk of each material or core slot in order (0 = the lowest that covers a bulk slot), the parts' ids. */
  | {
      readonly kind: 'craftStart';
      readonly schematic: number;
      readonly mks: readonly number[];
      readonly parts: readonly string[];
    }
  /** One experimentation point on a line. */
  | { readonly kind: 'craftPoint'; readonly line: number }
  /** The add-on, at a Mk. */
  | { readonly kind: 'craftEnhance'; readonly mk: number }
  /** Finish the craft of this schematic. */
  | { readonly kind: 'craftFinish'; readonly schematic: number }
  /** Repair an item at a station with a kit of its class (crafting-build.md C3). */
  | { readonly kind: 'repair'; readonly item: string }
  /** Salvage a crafted item at a station for a quarter of its quality materials. */
  | { readonly kind: 'salvage'; readonly item: string }
  /** Refine at a station (crafting-catalog.md § 11.1): N units of `template` at `mk` into one of the next, `count` times. */
  | { readonly kind: 'refine'; readonly template: number; readonly mk: number; readonly count: number }
  /** Pack an item or a crate into an identical one (crafting-catalog.md § 11.2), anywhere. */
  | { readonly kind: 'pack'; readonly item: string; readonly into: string }
  /** Take `count` items out of a crate, each into a free slot. */
  | { readonly kind: 'unpack'; readonly item: string; readonly count: number };

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
          item: itemOf(command.item),
        },
      };
    case 'vendor':
      return {
        name: 'SellToVendor',
        values: { template: command.template, mk: command.mk, quantity: command.quantity, item: itemOf(command.item) },
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

    case 'standard':
      return {
        name: 'BuyStandard',
        values: { template: command.template, price: command.price, quantity: command.quantity ?? 1 },
      };
    case 'equip': {
      const item = idOf(command.item);
      return item === null ? null : { name: 'Equip', values: { item } };
    }

    case 'unequip':
      return { name: 'Unequip', values: { slot: command.slot } };
    case 'craftStart': {
      const values: Record<string, number | bigint> = { schematic: command.schematic };
      for (let i = 0; i < 6; i++) {
        values[`mk${String(i)}`] = command.mks[i] ?? 0;
        values[`part${String(i)}`] = itemOf(command.parts[i]);
      }

      return { name: 'CraftStart', values };
    }

    case 'craftPoint':
      return { name: 'CraftExperiment', values: { line: command.line } };
    case 'craftEnhance':
      return { name: 'CraftEnhance', values: { mk: command.mk } };
    case 'craftFinish':
      return { name: 'CraftFinish', values: { schematic: command.schematic } };
    case 'repair':
      return { name: 'RepairItem', values: { item: itemOf(command.item) } };
    case 'salvage':
      return { name: 'SalvageItem', values: { item: itemOf(command.item) } };
    case 'refine':
      return { name: 'RefineItems', values: { template: command.template, mk: command.mk, count: command.count } };
    case 'pack':
      return { name: 'PackItem', values: { item: itemOf(command.item), into: itemOf(command.into) } };
    case 'unpack':
      return { name: 'UnpackItem', values: { item: itemOf(command.item), count: command.count } };
  }
}

/**
 * The row a command is about — where its refusal is shown: the listing, `'all'` for a collect-all, `'sell'` for the Sell tab, `'issue'` for standard
 * issue, `'gear'` for an equip or unequip.
 */
export function commandRow(command: MarketCommand): string {
  switch (command.kind) {
    case 'list':
    case 'vendor':
      return 'sell';
    case 'standard':
      return 'issue';
    case 'equip':
    case 'unequip':
    case 'pack':
    case 'unpack':
      return 'gear';
    case 'craftStart':
    case 'craftPoint':
    case 'craftEnhance':
    case 'craftFinish':
    case 'repair':
    case 'salvage':
    case 'refine':
      // One row for the workshop: its refusals show on the panel, and a command in flight holds the next — a double click spends one point.
      return 'craft';
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

/** An item's id for a command's `item` field: 0 for units (no id). */
function itemOf(text: string | undefined): bigint {
  return text === undefined ? 0n : (idOf(text) ?? 0n);
}

/** The bazaar panel's tabs (§ 6), and the vendor's standard issue (crafting-catalog.md § 15.3). */
export type BazaarTab = 'browse' | 'sell' | 'mine' | 'reserved' | 'issue';

/**
 * One thing the vendor sells, as `GET /swg/market/standard` answers it, Mk I at twice its value: an item with stats, one at a time; or a bulk material by
 * the unit (Loïc, 2026-11-02), `priceMilli` a unit — what no low-level creature drops, so a new crafter can start.
 */
export interface StandardOffer {
  readonly template: number;
  readonly item: string;
  readonly name: string;
  readonly category: string;
  readonly mk: number;
  readonly price: number;
  readonly stats?: ItemStats;
  readonly priceMilli?: number;
}

/** What `quantity` units of a vendor material cost, whole credits: the total rounded down, as the server prices it. */
export function standardMaterialPrice(offer: StandardOffer, quantity: number): number {
  return Math.floor(((offer.priceMilli ?? 0) * quantity) / 1000);
}

/** `GET /swg/market/standard` checked; `null` for anything else. A malformed row is dropped, not the answer. */
export function parseStandard(answer: unknown): readonly StandardOffer[] | null {
  if (typeof answer !== 'object' || answer === null || !Array.isArray((answer as Record<string, unknown>).items)) {
    return null;
  }

  return ((answer as Record<string, unknown>).items as unknown[]).filter((v): v is StandardOffer => {
    if (typeof v !== 'object' || v === null) {
      return false;
    }

    const o = v as Record<string, unknown>;
    return (
      typeof o.template === 'number' &&
      typeof o.item === 'string' &&
      typeof o.name === 'string' &&
      typeof o.price === 'number' &&
      ((typeof o.stats === 'object' && o.stats !== null) || typeof o.priceMilli === 'number')
    );
  });
}

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
    // The browser's own name by index: the server keeps Star Wars Galaxies' (world-data.ts), so its `cityName` is the fallback for an index this
    // client does not know, never the first choice — a row named "Mos Eisley" under a filter that says "Port Sable" was the same city twice.
    cityName: cityName(city) !== '' ? cityName(city) : typeof r.cityName === 'string' ? r.cityName : '',
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
