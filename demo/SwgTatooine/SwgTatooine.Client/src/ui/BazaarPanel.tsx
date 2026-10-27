import { ITEM_ICON_PX, itemInitials, mkNumeral } from '../data/items';
import {
  cityName,
  feeLine,
  fees,
  formatAge,
  MAX_UNIT_PRICE,
  priceOf,
  vendorPayout,
  type BazaarTab,
  type MarketCategory,
  type MarketListing,
  type MarketSort,
} from '../data/market';
import { formatCredits } from '../data/wallet';
import { CITIES } from '../data/world-data';
import { useItems } from '../state/items-store';
import { collectableHere, useMarket } from '../state/market-store';
import { useWallet } from '../state/wallet-store';

/**
 * The bazaar (bazaar.md § 6): the board of a city or of all, the stack being sold, this character's listings and the goods
 * waiting for it — deliberately plain, in the mission terminal's style.
 *
 * <b>Where it works</b> (B5): browsing and cancelling anywhere; buying, listing, collecting and the vendor at a bazaar
 * terminal, whose city is "here". Away from one those buttons are off and say why.
 *
 * <b>Nothing here is optimistic.</b> A button sends its command and its row waits; an accepted command moves the `items` or
 * `wallet` revision and the reads that follow redraw the rows, the balance and the grid. A refusal is written on the row it
 * was about — the race loser reads "Sold to someone else first" on the listing it clicked.
 */
export function BazaarPanel() {
  const open = useMarket((s) => s.open);
  const tab = useMarket((s) => s.tab);
  const here = useMarket((s) => s.here);
  const setTab = useMarket((s) => s.setTab);
  const close = useMarket((s) => s.close);
  const mine = useMarket((s) => s.mine);
  const credits = useWallet((s) => s.wallet?.credits ?? null);
  if (!open) {
    return null;
  }

  const tabButton = (t: BazaarTab, label: string) => (
    <button
      type="button"
      role="tab"
      aria-selected={tab === t}
      className={tab === t ? 'active' : ''}
      onClick={() => {
        setTab(t);
      }}
    >
      {label}
    </button>
  );

  return (
    <div className="panel bazaar" role="dialog" aria-label="Bazaar">
      <div className="missions-head">
        <span className="hud-title">Bazaar</span>
        <span className="bazaar-here" data-testid="bazaar-here">
          {here > 0 ? cityName(here) : 'not at a terminal'}
        </span>
        <span className="inventory-credits" data-testid="bazaar-credits">
          {credits === null ? '…' : formatCredits(credits)}
        </span>
        <button type="button" className="missions-close" onClick={close} aria-label="Close">
          ✕
        </button>
      </div>
      <div className="bazaar-tabs" role="tablist">
        {tabButton('browse', 'Browse')}
        {tabButton('sell', 'Sell')}
        {tabButton('mine', `My listings${mine === null ? '' : ` (${String(mine.listed.length)})`}`)}
        {tabButton('reserved', `Reserved${mine === null ? '' : ` (${String(mine.reserved.length)})`}`)}
      </div>
      {tab === 'browse' ? <BrowseTab /> : null}
      {tab === 'sell' ? <SellTab /> : null}
      {tab === 'mine' ? <MineTab /> : null}
      {tab === 'reserved' ? <ReservedTab /> : null}
    </div>
  );
}

const NOT_HERE = 'Stand at a bazaar terminal';

function BrowseTab() {
  const query = useMarket((s) => s.query);
  const page = useMarket((s) => s.page);
  const kept = useMarket((s) => s.kept);
  const here = useMarket((s) => s.here);
  const error = useMarket((s) => s.browseError);
  const prices = useMarket((s) => s.prices);
  const pending = useMarket((s) => s.pending);
  const notices = useMarket((s) => s.rowNotices);
  const mine = useMarket((s) => s.mine);
  const setQuery = useMarket((s) => s.setQuery);
  const buy = useMarket((s) => s.buy);
  const credits = useWallet((s) => s.wallet?.credits ?? null);
  const icons = useItems((s) => s.icons);
  const stacks = useMarket((s) => s.stacks);

  // The item filter: what the price list knows, by the name it states (or the board's, the inventory's — no item table here).
  const names = new Map<string, string>();
  for (const l of [...stacks, ...(page?.listings ?? [])]) {
    names.set(l.item, l.name);
  }

  const items = new Map<string, string>();
  for (const p of prices) {
    if (
      query.cat === '' ||
      p.category === query.cat ||
      (p.category === '' && p.item.startsWith(categoryPrefix(query.cat)))
    ) {
      items.set(p.item, p.name !== '' ? p.name : (names.get(p.item) ?? assetLabel(p.item)));
    }
  }

  const own = new Set(mine?.listed.map((l) => l.id) ?? []);
  const row = (l: MarketListing, gone: boolean) => {
    const notice = notices[l.id];
    const why =
      here === 0
        ? NOT_HERE
        : own.has(l.id)
          ? 'Your own listing'
          : credits !== null && l.total > credits
            ? 'Not enough credits'
            : 'Buy it for the total';
    return [
      <tr key={l.id} className={gone ? 'gone' : undefined} data-testid={`listing-${l.id}`}>
        <td>
          <ItemIcon item={l.item} name={l.name} icon={icons[l.item]} />
        </td>
        <td>{l.name}</td>
        <td className={`mk mk${String(l.mk)}`}>{mkNumeral(l.mk)}</td>
        <td className="num">{l.qty}</td>
        <td className="num">{formatCredits(l.unit)}</td>
        <td className="num">{formatCredits(l.total)}</td>
        <td>{l.cityName}</td>
        <td>{l.sellerName}</td>
        <td className="num">{formatAge(l.ageS)}</td>
        <td>
          <button
            type="button"
            title={why}
            disabled={
              gone || here === 0 || pending.includes(l.id) || own.has(l.id) || (credits !== null && l.total > credits)
            }
            onClick={() => {
              buy(l);
            }}
          >
            {pending.includes(l.id) ? '…' : 'Buy'}
          </button>
        </td>
      </tr>,
      notice === undefined ? null : (
        <tr key={`${l.id}-notice`} className="bazaar-row-notice">
          <td colSpan={10} role="status">
            {notice}
          </td>
        </tr>
      ),
    ];
  };

  return (
    <>
      <div className="row wrap bazaar-filters">
        <select
          aria-label="City"
          value={query.city}
          onChange={(e) => {
            setQuery({ city: Number(e.target.value) });
          }}
        >
          <option value={0}>All cities</option>
          {CITIES.map((c, i) => (
            <option key={c.name} value={i + 1}>
              {i + 1 === here ? `${c.name} (here)` : c.name}
            </option>
          ))}
        </select>
        <select
          aria-label="Category"
          value={query.cat}
          onChange={(e) => {
            setQuery({ cat: e.target.value as MarketCategory, item: '' });
          }}
        >
          <option value="">Any kind</option>
          <option value="material">Materials</option>
          <option value="core">Cores</option>
          <option value="addon">Add-ons</option>
        </select>
        <select
          aria-label="Item"
          value={query.item}
          onChange={(e) => {
            setQuery({ item: e.target.value });
          }}
        >
          <option value="">Any item</option>
          {[...items].map(([id, name]) => (
            <option key={id} value={id}>
              {name}
            </option>
          ))}
        </select>
        <select
          aria-label="Mk"
          value={query.mk}
          onChange={(e) => {
            setQuery({ mk: Number(e.target.value) });
          }}
        >
          <option value={0}>Any Mk</option>
          {[1, 2, 3, 4, 5].map((mk) => (
            <option key={mk} value={mk}>
              Mk {mkNumeral(mk)}
            </option>
          ))}
        </select>
        <input
          type="number"
          aria-label="Max unit price"
          placeholder="max cr"
          min={0}
          max={MAX_UNIT_PRICE}
          value={query.max === 0 ? '' : query.max}
          onChange={(e) => {
            setQuery({ max: Math.max(0, Math.floor(Number(e.target.value) || 0)) });
          }}
        />
        <select
          aria-label="Sort"
          value={query.sort}
          onChange={(e) => {
            setQuery({ sort: e.target.value as MarketSort });
          }}
        >
          <option value="price">Cheapest</option>
          <option value="-price">Dearest</option>
          <option value="new">Newest</option>
          <option value="mk">Highest Mk</option>
        </select>
      </div>
      {error !== null ? <div className="missions-notice">{error}</div> : null}
      <table className="missions-table bazaar-table">
        <thead>
          <tr>
            <th />
            <th>Item</th>
            <th>Mk</th>
            <th>Qty</th>
            <th>Unit</th>
            <th>Total</th>
            <th>City</th>
            <th>Seller</th>
            <th>Age</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {page?.listings.map((l) => row(l, false))}
          {kept.map((l) => row(l, true))}
        </tbody>
      </table>
      {page !== null && page.listings.length === 0 ? <div className="hint">Nothing listed that matches.</div> : null}
      {page === null && error === null ? <div className="hint">Reading the board…</div> : null}
      {page !== null ? (
        <div className="row bazaar-pager">
          <button
            type="button"
            disabled={page.page <= 1}
            onClick={() => {
              setQuery({ page: page.page - 1 });
            }}
          >
            ‹ Prev
          </button>
          <span className="hint">
            Page {page.page} / {page.pages} · {page.total} listed
          </span>
          <button
            type="button"
            disabled={page.page >= page.pages}
            onClick={() => {
              setQuery({ page: page.page + 1 });
            }}
          >
            Next ›
          </button>
        </div>
      ) : null}
    </>
  );
}

function SellTab() {
  const stacks = useMarket((s) => s.stacks);
  const sell = useMarket((s) => s.sell);
  const here = useMarket((s) => s.here);
  const prices = useMarket((s) => s.prices);
  const pending = useMarket((s) => s.pending.includes('sell'));
  const notice = useMarket((s) => s.rowNotices.sell);
  const mine = useMarket((s) => s.mine);
  const selectStack = useMarket((s) => s.selectStack);
  const setQuantity = useMarket((s) => s.setSellQuantity);
  const setPrice = useMarket((s) => s.setSellPrice);
  const list = useMarket((s) => s.list);
  const vendor = useMarket((s) => s.vendor);
  const credits = useWallet((s) => s.wallet?.credits ?? null);
  const icons = useItems((s) => s.icons);

  const stack = stacks.find((s) => s.id === sell.stack);
  const price = stack === undefined ? undefined : priceOf(prices, stack.item, stack.mk);
  const total = sell.quantity * sell.unitPrice;
  const fee = fees(total).listFee;
  const payout = price === undefined ? 0 : vendorPayout(sell.quantity, price.vendor);
  const full = mine !== null && mine.listed.length >= mine.caps.listings;
  return (
    <>
      <div className="row">
        <select
          aria-label="Stack to sell"
          value={sell.stack ?? ''}
          onChange={(e) => {
            selectStack(e.target.value);
          }}
        >
          <option value="" disabled>
            Pick a stack…
          </option>
          {stacks.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
              {s.mk > 0 ? ` Mk ${mkNumeral(s.mk)}` : ''} ×{s.count}
            </option>
          ))}
        </select>
      </div>
      {stack === undefined ? (
        <div className="hint">Pick a stack here, or click one in your inventory (I).</div>
      ) : (
        <>
          <div className="row bazaar-picked">
            <ItemIcon item={stack.item} name={stack.name} icon={icons[stack.item]} />
            <span>
              {stack.name}
              {stack.mk > 0 ? ` Mk ${mkNumeral(stack.mk)}` : ''} · {stack.count} held
            </span>
          </div>
          <div className="row">
            <label>
              Quantity{' '}
              <input
                type="number"
                aria-label="Quantity"
                min={1}
                max={stack.count}
                value={sell.quantity}
                onChange={(e) => {
                  setQuantity(Number(e.target.value));
                }}
              />
            </label>
            <label>
              Unit price{' '}
              <input
                type="number"
                aria-label="Unit price"
                min={1}
                max={MAX_UNIT_PRICE}
                value={sell.unitPrice}
                onChange={(e) => {
                  setPrice(Number(e.target.value));
                }}
              />
            </label>
          </div>
          <div className="hint" data-testid="price-hints">
            {price === undefined
              ? 'No market price known'
              : `value ${formatCredits(price.value)} · lowest listed ${price.lowest > 0 ? formatCredits(price.lowest) : '—'} · median ${price.median > 0 ? formatCredits(price.median) : '—'}`}
          </div>
          <div className="hint" data-testid="fee-preview">
            {formatCredits(total)} total · {feeLine(total)}
          </div>
          <div className="row">
            <button
              type="button"
              disabled={here === 0 || pending || full || (credits !== null && fee > credits)}
              title={
                here === 0 ? NOT_HERE : full ? 'You have the most listings allowed' : `Pay ${String(fee)} cr and list`
              }
              onClick={list}
            >
              {pending ? '…' : 'List'}
            </button>
            <button
              type="button"
              disabled={here === 0 || pending || payout === 0}
              title={here === 0 ? NOT_HERE : payout === 0 ? 'The vendor will not buy that' : 'The units are destroyed'}
              onClick={vendor}
            >
              {payout === 0 ? 'The vendor will not buy this' : `Sell to vendor for ${formatCredits(payout)}`}
            </button>
          </div>
        </>
      )}
      {notice !== undefined ? (
        <div className="missions-notice" role="status">
          {notice}
        </div>
      ) : null}
    </>
  );
}

function MineTab() {
  const mine = useMarket((s) => s.mine);
  const pending = useMarket((s) => s.pending);
  const notices = useMarket((s) => s.rowNotices);
  const cancel = useMarket((s) => s.cancel);
  const icons = useItems((s) => s.icons);
  if (mine === null) {
    return <div className="hint">Reading your listings…</div>;
  }

  return (
    <>
      <div className="hint">
        {mine.listed.length}/{mine.caps.listings} listed · cancel anywhere: the goods wait for you at their city
      </div>
      <table className="missions-table bazaar-table">
        <tbody>
          {mine.listed.map((l) => [
            <tr key={l.id} data-testid={`mine-${l.id}`}>
              <td>
                <ItemIcon item={l.item} name={l.name} icon={icons[l.item]} />
              </td>
              <td>{l.name}</td>
              <td className={`mk mk${String(l.mk)}`}>{mkNumeral(l.mk)}</td>
              <td className="num">{l.qty}</td>
              <td className="num">{formatCredits(l.unit)}</td>
              <td className="num">{formatCredits(l.total)}</td>
              <td>{l.cityName}</td>
              <td className="num">{formatAge(l.ageS)}</td>
              <td>
                <button
                  type="button"
                  disabled={pending.includes(l.id)}
                  onClick={() => {
                    cancel(l.id);
                  }}
                >
                  {pending.includes(l.id) ? '…' : 'Cancel'}
                </button>
              </td>
            </tr>,
            notices[l.id] === undefined ? null : (
              <tr key={`${l.id}-notice`} className="bazaar-row-notice">
                <td colSpan={9} role="status">
                  {notices[l.id]}
                </td>
              </tr>
            ),
          ])}
        </tbody>
      </table>
      {mine.listed.length === 0 ? <div className="hint">Nothing listed.</div> : null}
    </>
  );
}

function ReservedTab() {
  const mine = useMarket((s) => s.mine);
  const here = useMarket((s) => s.here);
  const hereRealm = useMarket((s) => s.hereRealm);
  const pending = useMarket((s) => s.pending);
  const notices = useMarket((s) => s.rowNotices);
  const collect = useMarket((s) => s.collect);
  const icons = useItems((s) => s.icons);
  if (mine === null) {
    return <div className="hint">Reading what waits for you…</div>;
  }

  const atHere = mine.reserved.filter((r) => collectableHere(r, here, hereRealm)).length;
  return (
    <>
      <div className="hint">
        {mine.reserved.length}/{mine.caps.reserved} waiting · collected at a bazaar terminal of their city
      </div>
      <table className="missions-table bazaar-table">
        <tbody>
          {mine.reserved.map((r) => [
            <tr key={r.id} data-testid={`reserved-${r.id}`}>
              <td>
                <ItemIcon item={r.item} name={r.name} icon={icons[r.item]} />
              </td>
              <td>{r.name}</td>
              <td className={`mk mk${String(r.mk)}`}>{mkNumeral(r.mk)}</td>
              <td className="num">{r.qty}</td>
              <td>{r.state === 'sold' ? 'Bought' : 'Returned'}</td>
              <td>{r.cityName}</td>
              <td>
                {collectableHere(r, here, hereRealm) ? (
                  <button
                    type="button"
                    disabled={pending.includes(r.id)}
                    onClick={() => {
                      collect(r.id);
                    }}
                  >
                    {pending.includes(r.id) ? '…' : 'Collect'}
                  </button>
                ) : (
                  <span className="hint">collect at {r.cityName}</span>
                )}
              </td>
            </tr>,
            notices[r.id] === undefined ? null : (
              <tr key={`${r.id}-notice`} className="bazaar-row-notice">
                <td colSpan={7} role="status">
                  {notices[r.id]}
                </td>
              </tr>
            ),
          ])}
        </tbody>
      </table>
      {mine.reserved.length === 0 ? <div className="hint">Nothing waiting.</div> : null}
      {atHere > 1 ? (
        <div className="row">
          <button
            type="button"
            disabled={pending.includes('all')}
            onClick={() => {
              collect('0');
            }}
          >
            Collect all ({atHere}) here
          </button>
        </div>
      ) : null}
      {notices.all !== undefined ? (
        <div className="missions-notice" role="status">
          {notices.all}
        </div>
      ) : null}
    </>
  );
}

/** An item's icon at the catalogue's 32 px, or its initials without one — the inventory grid's rule. */
function ItemIcon({
  item,
  name,
  icon,
}: {
  readonly item: string;
  readonly name: string;
  readonly icon: string | undefined;
}) {
  return icon === undefined ? (
    <span className="inventory-initials" data-item={item}>
      {itemInitials(name)}
    </span>
  ) : (
    <img
      className="inventory-icon"
      src={icon}
      width={ITEM_ICON_PX}
      height={ITEM_ICON_PX}
      alt=""
      draggable={false}
      data-item={item}
    />
  );
}

/** The asset-id prefix of a category: the board's item filter lists only that category's items. */
function categoryPrefix(cat: MarketCategory): string {
  return cat === 'material' ? 'mat_' : cat === 'core' ? 'core_' : cat === 'addon' ? 'addon_' : '';
}

/** An asset id no row has named yet, readable: `core_armor` → "armor (core)", `mat_gas` → "gas (material)". */
function assetLabel(id: string): string {
  const [prefix = '', ...rest] = id.split('_');
  const kind = prefix === 'mat' ? 'material' : prefix === 'addon' ? 'add-on' : prefix;
  return rest.length === 0 ? id : `${rest.join(' ')} (${kind})`;
}
