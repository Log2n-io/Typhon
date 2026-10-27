import { ITEM_ICON_PX, itemInitials, itemTooltip, mkNumeral, slotGrid, type InventoryItem } from '../data/items';
import { formatCredits } from '../data/wallet';
import { useItems } from '../state/items-store';
import { useMarket } from '../state/market-store';
import { useMissions } from '../state/mission-store';
import { useWallet } from '../state/wallet-store';

/** The item grid's slots before the first read; the server's answer carries its own (`slots`). */
export const INVENTORY_SLOTS = 30;

/** How many of the wallet's recent transfers the window lists. */
const RECENT_SHOWN = 6;

const KIND_NAMES: Readonly<Record<string, string>> = {
  grant: 'Starting credits',
  mission: 'Mission reward',
  tip: 'Tip',
  // The bazaar's (bazaar.md § 2.2).
  sale: 'Bazaar sale',
  tax: 'Sales tax',
  listfee: 'Listing fee',
  vendor: 'Vendor',
};

/** A transfer's line in the window: a `sale` row is the buyer's purchase when credits left this character, the seller's sale when they came in. */
export function transferLabel(kind: string, amount: number): string {
  if (kind === 'sale' && amount < 0) {
    return 'Bazaar purchase';
  }

  return KIND_NAMES[kind] ?? kind;
}

/**
 * The inventory (economy-foundation.md E0-10, inventory-loot.md § 6): the character's items in their slots and its credits
 * at the top right, with the last few transfers under the grid. Toggled by <kbd>I</kbd> and by the bottom-right toolbar.
 *
 * The balance is the wallet as last read (`WalletReader`), which re-reads it whenever a transfer touches this character;
 * the items are the inventory as last read, re-read whenever the body's `items` revision moves.
 *
 * <b>With the bazaar open</b> (bazaar.md § 6), a click on a stack picks it for the Sell tab. Units in a listing are not in
 * the grid: they left the inventory when it was listed.
 */
export function InventoryWindow() {
  const open = useWallet((s) => s.inventoryOpen);
  const wallet = useWallet((s) => s.wallet);
  const close = useWallet((s) => s.closeInventory);
  const journalOpen = useMissions((s) => s.journalOpen);
  const inventory = useItems((s) => s.snapshot);
  const icons = useItems((s) => s.icons);
  const bazaarOpen = useMarket((s) => s.open);
  const pick = useMarket((s) => s.sell.stack);
  if (!open) {
    return null;
  }

  const onPick = bazaarOpen
    ? (id: string) => {
        const market = useMarket.getState();
        market.selectStack(id);
        market.setTab('sell');
      }
    : null;

  const recent = wallet?.transfers.slice(0, RECENT_SHOWN) ?? [];
  const cells = slotGrid(inventory?.items ?? [], inventory?.slots ?? INVENTORY_SLOTS);
  return (
    <div
      className={`panel inventory${journalOpen ? ' beside-journal' : ''}${bazaarOpen ? ' beside-bazaar' : ''}`}
      role="dialog"
      aria-label="Inventory"
    >
      <div className="missions-head">
        <span className="hud-title">Inventory</span>
        <span className="inventory-credits" data-testid="inventory-credits">
          {wallet === null ? '…' : formatCredits(wallet.credits)}
        </span>
        <button type="button" className="missions-close" onClick={close} aria-label="Close">
          ✕
        </button>
      </div>
      <div className="inventory-grid" aria-label="Items">
        {cells.map((item, i) =>
          item === null ? (
            <div key={i} className="inventory-slot" />
          ) : (
            <ItemSlot
              key={i}
              item={item}
              icon={icons[item.item]}
              picked={bazaarOpen && item.id === pick}
              onPick={onPick}
            />
          ),
        )}
      </div>
      {cells.every((c) => c === null) ? <div className="hint">No items yet.</div> : null}
      {recent.length > 0 ? (
        <div className="inventory-history">
          {recent.map((t) => (
            <div className="inventory-transfer" key={t.seq}>
              <span className="inventory-kind">
                {transferLabel(t.kind, t.amount)}
                {t.kind === 'tip' && t.other !== null ? (t.amount > 0 ? ` from ${t.other}` : ` to ${t.other}`) : ''}
              </span>
              <span className={t.amount >= 0 ? 'inventory-amount in' : 'inventory-amount out'}>
                {t.amount >= 0 ? '+' : '−'}
                {formatCredits(Math.abs(t.amount))}
              </span>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/**
 * One stack: its icon at the catalogue's 32 px (never scaled; the name's initials without one), its Mk as a numeral coloured
 * like the model's Mk material (ASSET_FORMAT.md § 5.11), its count when more than one, and on hover its name, Mk and count.
 */
function ItemSlot({
  item,
  icon,
  picked,
  onPick,
}: {
  readonly item: InventoryItem;
  readonly icon: string | undefined;
  /** Whether it is the stack the bazaar's Sell tab holds. */
  readonly picked: boolean;
  /** Picks it for the bazaar's Sell tab; `null` with the bazaar closed. */
  readonly onPick: ((id: string) => void) | null;
}) {
  const tip = itemTooltip(item);
  const mk = mkNumeral(item.mk);
  return (
    <div
      className={
        picked
          ? 'inventory-slot filled picked'
          : onPick === null
            ? 'inventory-slot filled'
            : 'inventory-slot filled pickable'
      }
      data-testid={`inventory-slot-${String(item.slot)}`}
      data-item={item.item}
      aria-label={tip}
      onClick={
        onPick === null
          ? undefined
          : () => {
              onPick(item.id);
            }
      }
    >
      {icon === undefined ? (
        <span className="inventory-initials">{itemInitials(item.name)}</span>
      ) : (
        <img
          className="inventory-icon"
          src={icon}
          width={ITEM_ICON_PX}
          height={ITEM_ICON_PX}
          alt=""
          draggable={false}
        />
      )}
      {mk === '' ? null : <span className={`inventory-mk mk${String(item.mk)}`}>{mk}</span>}
      {item.count > 1 ? <span className="inventory-count">{item.count}</span> : null}
      <span className="inventory-tip" role="tooltip">
        {tip}
      </span>
    </div>
  );
}
