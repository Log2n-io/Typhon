import { useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

import { ITEM_ICON_PX, itemInitials, itemTooltip, mkNumeral, slotGrid, type InventoryItem } from '../data/items';
import { cratesOf, packSteps } from '../data/crates';
import { formatCredits } from '../data/wallet';
import { useItems } from '../state/items-store';
import { useMarket } from '../state/market-store';
import { useWallet } from '../state/wallet-store';
import { WindowFrame } from './WindowFrame';

/** The item grid's slots before the first read; the server's answer carries its own (`slots`). */
export const INVENTORY_SLOTS = 40;

/**
 * The inventory (economy-foundation.md E0-10, inventory-loot.md § 6): the character's items in their slots and its credits
 * at the top right. Toggled by <kbd>I</kbd> and by the bottom-right toolbar. No history: the ledger is the operators', never
 * the player's (Loïc, 2026-10-29); a move is said once in the chat, from the server's notice.
 *
 * The balance is the wallet as last read (`WalletReader`), which re-reads it whenever a transfer touches this character;
 * the items are the inventory as last read, re-read whenever the body's `items` revision moves.
 *
 * <b>With the bazaar open</b> (bazaar.md § 6), a click on a stack picks it for the Sell tab. Units in a listing are not in
 * the grid: they left the inventory when it was listed.
 *
 * <b>What it wears is the character window's</b> (client.md C39): a click on an item with stats in the grid puts it on — swapped with what was worn in
 * its slot — and so does dragging it onto the Equipment tab's slot. The server refuses below the Mk's combat level, and says why here.
 */
export function InventoryWindow() {
  const open = useWallet((s) => s.inventoryOpen);
  const wallet = useWallet((s) => s.wallet);
  const close = useWallet((s) => s.closeInventory);
  const inventory = useItems((s) => s.snapshot);
  const icons = useItems((s) => s.icons);
  const bazaarOpen = useMarket((s) => s.open);
  const pick = useMarket((s) => s.sell.stack);
  const gearNotice = useMarket((s) => s.rowNotices.gear);
  if (!open) {
    return null;
  }

  const onPick = bazaarOpen
    ? (id: string) => {
        const market = useMarket.getState();
        market.selectStack(id);
        market.setTab('sell');
      }
    : (id: string) => {
        const item = inventory?.items.find((i) => i.id === id);
        if (item?.stats !== undefined) {
          useMarket.getState().equip(id);
        }
      };

  const cells = slotGrid(inventory?.items ?? [], inventory?.slots ?? INVENTORY_SLOTS);
  return (
    <WindowFrame
      id="inventory"
      title="Inventory"
      extra={
        <>
          <span className="inventory-credits" data-testid="inventory-credits">
            {wallet === null ? '…' : formatCredits(wallet.credits)}
          </span>
        </>
      }
      onClose={close}
      ariaLabel="Inventory"
      className="inventory"
      minW={260}
      initial={(vw, vh) => ({ x: vw - 634, y: Math.round(vh * 0.25), w: 322, h: 0 })}
    >
      {gearNotice === undefined ? null : (
        <div className="hint" role="alert">
          {gearNotice}
        </div>
      )}
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
      <Crates items={inventory?.items ?? NO_ITEMS} />
    </WindowFrame>
  );
}

const NO_ITEMS: readonly InventoryItem[] = [];

/**
 * Crates (crafting-catalog.md § 11.2): identical unused items packed into one slot, ten at most — a pack a click, the smallest into the fullest; a crate
 * unpacked one item, or all of it, into free slots. A crate is listed and sold whole, and unpacked before it is worn.
 */
function Crates({ items }: { readonly items: readonly InventoryItem[] }) {
  const pending = useMarket((s) => s.pending.includes('gear'));
  const steps = packSteps(items);
  const crates = cratesOf(items);
  if (steps.length === 0 && crates.length === 0) {
    return null;
  }

  const label = (name: string, mk: number): string => `${name}${mk > 0 ? ` Mk ${mkNumeral(mk)}` : ''}`;
  return (
    <div className="inventory-crates" aria-label="Crates">
      <div className="hud-title">Crates</div>
      {steps.map((s) => (
        <div key={s.into} className="row">
          <span>
            {label(s.name, s.mk)} · {String(s.held)} identical
          </span>
          <button
            type="button"
            disabled={pending}
            onClick={() => {
              useMarket.getState().pack(s.item, s.into);
            }}
          >
            Pack ({String(s.total)} in a crate)
          </button>
        </div>
      ))}
      {crates.map((c) => (
        <div key={c.id} className="row">
          <span>
            Crate: {label(c.name, c.mk)} ×{String(c.count)}
          </span>
          <button
            type="button"
            disabled={pending}
            onClick={() => {
              useMarket.getState().unpack(c.id, 1);
            }}
          >
            Unpack 1
          </button>
          <button
            type="button"
            disabled={pending}
            onClick={() => {
              useMarket.getState().unpack(c.id, c.count - 1);
            }}
          >
            Unpack all
          </button>
        </div>
      ))}
    </div>
  );
}

/**
 * One stack: its icon at the catalogue's 32 px (never scaled; the name's initials without one), its Mk as a numeral coloured
 * like the model's Mk material (ASSET_FORMAT.md § 5.11), its count when more than one, and on hover its name, Mk and count.
 */
/** The drag's data type: an item's raw id, dropped on an equip slot to wear it (client.md C39). */
export const ITEM_DRAG_TYPE = 'application/x-swg-item';

export function ItemSlot({
  item,
  icon,
  picked,
  onPick,
}: {
  readonly item: InventoryItem;
  readonly icon: string | undefined;
  /** Whether it is the stack the bazaar's Sell tab holds. */
  readonly picked: boolean;
  /** Picks it for the bazaar's Sell tab with the bazaar open; wears or takes it off with it closed. */
  readonly onPick: ((id: string) => void) | null;
}) {
  // An item that is worn somewhere drags to its slot; units and parts do not drag.
  const wearable = (item.stats?.wears ?? 0) > 0 && item.slot <= 100;
  const tip = itemTooltip(item);
  const mk = mkNumeral(item.mk);
  const [hovered, setHovered] = useState<DOMRect | null>(null);
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
      draggable={wearable}
      onDragStart={
        wearable
          ? (e) => {
              e.dataTransfer.setData(ITEM_DRAG_TYPE, item.id);
              e.dataTransfer.effectAllowed = 'move';
              setHovered(null);
            }
          : undefined
      }
      onMouseEnter={(e) => {
        setHovered(e.currentTarget.getBoundingClientRect());
      }}
      onMouseLeave={() => {
        setHovered(null);
      }}
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
      {hovered === null ? null : <ItemTip text={tip} anchor={hovered} />}
    </div>
  );
}

/** Margin kept between the tooltip and the window edge, and between it and the slot it names. */
const TIP_GAP_PX = 4;

/**
 * A stack's tooltip, in a portal on the document body with fixed positioning: the inventory window scrolls
 * (`overflow-y: auto`), which clips horizontally as well, so a tooltip inside it was cut at the window's edge. Centred above
 * the slot, clamped inside the viewport, and below the slot when there is no room above.
 */
function ItemTip({ text, anchor }: { readonly text: string; readonly anchor: DOMRect }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [at, setAt] = useState<{ left: number; top: number } | null>(null);
  useLayoutEffect(() => {
    const tip = ref.current;
    if (tip === null) {
      return;
    }

    setAt(tipPosition(anchor, tip.offsetWidth, tip.offsetHeight, window.innerWidth));
  }, [anchor, text]);
  return createPortal(
    <span
      ref={ref}
      className="inventory-tip"
      role="tooltip"
      style={at === null ? { visibility: 'hidden', left: 0, top: 0 } : { left: at.left, top: at.top }}
    >
      {text}
    </span>,
    document.body,
  );
}

/** Where a tooltip of `width` × `height` goes for a slot at `anchor`: centred above it, inside the viewport, below it when the top is too close. */
export function tipPosition(
  anchor: { readonly left: number; readonly top: number; readonly width: number; readonly bottom: number },
  width: number,
  height: number,
  viewportWidth: number,
): { left: number; top: number } {
  const centred = anchor.left + anchor.width / 2 - width / 2;
  const left = Math.max(TIP_GAP_PX, Math.min(centred, viewportWidth - width - TIP_GAP_PX));
  const above = anchor.top - height - TIP_GAP_PX;
  return { left, top: above >= TIP_GAP_PX ? above : anchor.bottom + TIP_GAP_PX };
}
