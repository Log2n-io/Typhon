import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

import { itemMenuItems } from '../data/item-menu';
import {
  dropOnSlot,
  freeSlots,
  isUnits,
  previewLines,
  promptCount,
  promptRange,
  type ItemPrompt,
} from '../data/inventory-grid';
import {
  ITEM_ICON_PX,
  itemInitials,
  itemTooltip,
  mkNumeral,
  slotCount,
  slotGrid,
  type InventoryItem,
} from '../data/items';
import { formatCredits } from '../data/wallet';
import type { WindowRect } from '../data/ui-layout';
import { useItems } from '../state/items-store';
import { useMarket } from '../state/market-store';
import { useUi } from '../state/ui-store';
import { useWallet } from '../state/wallet-store';
import { useWindows } from '../state/windows-store';
import { ItemPreview } from './ItemPreview';
import { WindowFrame } from './WindowFrame';

/** The item grid's slots before the first read; the server's answer carries its own (`slots`). */
export const INVENTORY_SLOTS = 50;

/** The preview panel's width, px: the window grows by it when the panel opens, and shrinks back when it closes. */
export const PREVIEW_PANEL_W = 280;

const initialRect = (vw: number, vh: number): WindowRect => ({ x: vw - 634, y: Math.round(vh * 0.25), w: 322, h: 0 });

/**
 * The inventory (economy-foundation.md E0-10, inventory-loot.md § 6, client.md C43): the character's items in their slots and its credits at the top
 * right. Toggled by <kbd>I</kbd> and by the bottom-right toolbar. No history: the ledger is the operators', never the player's (Loïc, 2026-10-29).
 *
 * <b>Slots keep their size</b>: resizing the window changes how many fit on a row, never how big one is — an icon is drawn at the size it was rendered.
 *
 * <b>A click selects</b>, and the preview panel (<b>&gt;&gt;</b>) shows the selected item turning, with its stats. <b>A drag moves it</b>: onto a free slot,
 * onto the same units (merged, as much as the stack there holds), onto an identical item or crate with room (packed), or onto anything else (swapped);
 * with Shift held onto a free slot, it asks how many to split off. <b>A double click wears it</b>, as dragging it onto the character window's slot does
 * (client.md C39). With the bazaar open, a click also picks it for the Sell tab (bazaar.md § 6).
 *
 * <b>Sort &amp; stack</b> (Loïc, 2026-11-04), in the title bar, asks the server to merge every stack of the same units and lay the grid out by type, name and
 * mark from the first slot.
 *
 * <b>Everything else is the item's own menu</b> (client.md C42): a right-click on a slot offers what that item does — placed, worn, packed, unpacked,
 * split, thrown away. Those that need a count open the prompt under the grid; throwing away always does, as its confirmation. The window lists; the item
 * acts.
 */
export function InventoryWindow() {
  const open = useWallet((s) => s.inventoryOpen);
  const wallet = useWallet((s) => s.wallet);
  const close = useWallet((s) => s.closeInventory);
  const inventory = useItems((s) => s.snapshot);
  const icons = useItems((s) => s.icons);
  const selected = useItems((s) => s.selected);
  const previewOpen = useWindows((s) => s.layout.inventoryPreview);
  const prompt = useItems((s) => s.prompt);
  const bazaarOpen = useMarket((s) => s.open);
  const pick = useMarket((s) => s.sell.stack);
  const gearNotice = useMarket((s) => s.rowNotices.gear);
  const [over, setOver] = useState<number | null>(null);
  if (!open) {
    return null;
  }

  const items = inventory?.items ?? [];
  const slots = inventory?.slots ?? INVENTORY_SLOTS;
  const free = freeSlots(items, slots).length;
  const find = (id: string | null): InventoryItem | undefined =>
    id === null ? undefined : items.find((i) => i.id === id && i.slot <= 100);

  const onSelect = (id: string): void => {
    useItems.getState().select(id);
    if (bazaarOpen) {
      const market = useMarket.getState();
      market.selectStack(id);
      market.setTab('sell');
    }
  };

  const onActivate = (id: string): void => {
    const item = find(id);
    if (item?.stats !== undefined && (item.stats.wears ?? 0) > 0 && item.count === 1) {
      useMarket.getState().equip(id);
    }
  };

  // The item's menu, at the pointer (client.md C42): nothing opens for an item that offers nothing.
  const onMenu = (item: InventoryItem, x: number, y: number): void => {
    useItems.getState().select(item.id);
    const entries = itemMenuItems(item, items, free);
    if (entries.length > 0) {
      useUi.getState().openContextMenu({ x, y, title: item.name, source: 'item', items: entries });
    }
  };

  const onDrop = (slot: number, there: InventoryItem | null, id: string, shift: boolean): void => {
    const item = find(id);
    if (item === undefined) {
      return;
    }

    if (shift && there === null && isUnits(item) && item.count > 1) {
      useItems.getState().openPrompt({ mode: 'split', item: item.id, slot });
      return;
    }

    const drop = dropOnSlot(item, slot, there);
    if (drop?.kind === 'move') {
      useMarket.getState().move(drop.item, drop.slot, drop.count);
    } else if (drop?.kind === 'pack') {
      useMarket.getState().pack(drop.item, drop.into);
    }
  };

  const togglePreview = (): void => {
    const windows = useWindows.getState();
    const rect = windows.layout.windows.inventory ?? initialRect(window.innerWidth, window.innerHeight);
    windows.setInventoryPreview(!previewOpen, {
      ...rect,
      w: Math.max(260, rect.w + (previewOpen ? -PREVIEW_PANEL_W : PREVIEW_PANEL_W)),
    });
  };

  const cells = slotGrid(items, slots);
  const shown = find(selected);
  const prompted = prompt === null ? undefined : find(prompt.item);
  return (
    <WindowFrame
      id="inventory"
      title="Inventory"
      extra={
        <>
          <span className="inventory-credits" data-testid="inventory-credits">
            {wallet === null ? '…' : formatCredits(wallet.credits)}
          </span>
          <button
            type="button"
            className="inventory-expand"
            disabled={items.every((i) => i.slot > 100)}
            title="Stack the same items together, then sort the grid by type, name and mark"
            onClick={() => {
              useMarket.getState().sort();
            }}
          >
            Sort &amp; stack
          </button>
          <button
            type="button"
            className="inventory-expand"
            aria-pressed={previewOpen}
            aria-label={previewOpen ? 'Hide the item preview' : 'Show the item preview'}
            title={previewOpen ? 'Hide the item preview' : 'Show the selected item: its model and its stats'}
            onClick={togglePreview}
          >
            {previewOpen ? '<<' : '>>'}
          </button>
        </>
      }
      onClose={close}
      ariaLabel="Inventory"
      className="inventory"
      minW={260}
      initial={initialRect}
    >
      <div className="inventory-layout">
        <div className="inventory-main">
          {gearNotice === undefined ? null : (
            <div className="hint" role="alert">
              {gearNotice}
            </div>
          )}
          <div className="inventory-grid" role="listbox" aria-label="Items">
            {cells.map((item, i) => {
              const slot = i + 1;
              const drop = {
                over: over === slot,
                onOver: () => {
                  setOver(slot);
                },
                onLeave: () => {
                  setOver((o) => (o === slot ? null : o));
                },
                onDrop: (id: string, shift: boolean) => {
                  setOver(null);
                  onDrop(slot, item, id, shift);
                },
              };
              return item === null ? (
                <EmptySlot key={i} slot={slot} drop={drop} />
              ) : (
                <ItemSlot
                  key={i}
                  item={item}
                  icon={icons[item.item]}
                  picked={bazaarOpen && item.id === pick}
                  selected={item.id === selected}
                  onPick={onSelect}
                  onActivate={onActivate}
                  onMenu={onMenu}
                  drag
                  drop={drop}
                />
              );
            })}
          </div>
          {cells.every((c) => c === null) ? <div className="hint">No items yet.</div> : null}
          {prompt === null || prompted === undefined ? null : (
            <CountPrompt key={`${prompt.mode}:${prompt.item}`} prompt={prompt} item={prompted} free={free} />
          )}
        </div>
        {previewOpen ? <ItemDetails item={shown} /> : null}
      </div>
    </WindowFrame>
  );
}

/** A drop target's state and handlers: the slot it is, highlighted while something is dragged over it. */
interface DropTarget {
  readonly over: boolean;
  readonly onOver: () => void;
  readonly onLeave: () => void;
  /** The dragged item's id, and whether Shift was held. */
  readonly onDrop: (id: string, shift: boolean) => void;
}

/** The handlers that make an element a slot items are dropped on. */
function dropHandlers(drop: DropTarget | undefined) {
  return drop === undefined
    ? {}
    : {
        onDragOver: (e: React.DragEvent) => {
          if (e.dataTransfer.types.includes(ITEM_DRAG_TYPE)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            drop.onOver();
          }
        },
        onDragLeave: () => {
          drop.onLeave();
        },
        onDrop: (e: React.DragEvent) => {
          const id = e.dataTransfer.getData(ITEM_DRAG_TYPE);
          if (id !== '') {
            e.preventDefault();
            drop.onDrop(id, e.shiftKey);
          }
        },
      };
}

function EmptySlot({ slot, drop }: { readonly slot: number; readonly drop: DropTarget }) {
  return (
    <div
      className={drop.over ? 'inventory-slot drop' : 'inventory-slot'}
      data-testid={`inventory-slot-${String(slot)}`}
      role="option"
      aria-label={`Slot ${String(slot)}, empty`}
      aria-selected={false}
      {...dropHandlers(drop)}
    />
  );
}

/** The drag's data type: an item's raw id, dropped on a grid slot to move it or on an equip slot to wear it (client.md C39, C43). */
export const ITEM_DRAG_TYPE = 'application/x-swg-item';

/**
 * One stack: its icon at the catalogue's 32 px (never scaled; the name's initials without one), its Mk as a numeral coloured like the model's Mk material
 * (ASSET_FORMAT.md § 5.11), its count when more than one (`12.3K` past 9 999), and on hover its name, Mk and exact count.
 */
export function ItemSlot({
  item,
  icon,
  picked,
  selected = false,
  onPick,
  onActivate,
  onMenu,
  drag = false,
  drop,
}: {
  readonly item: InventoryItem;
  readonly icon: string | undefined;
  /** Whether it is the stack the bazaar's Sell tab holds. */
  readonly picked: boolean;
  /** Whether it is the window's selected item, the preview's. */
  readonly selected?: boolean;
  /** A click: selects it in the grid; takes it off on the character window's equip strip. */
  readonly onPick: ((id: string) => void) | null;
  /** A double click: wears it. */
  readonly onActivate?: (id: string) => void;
  /** Opens the item's own menu at the pointer (client.md C42); none where the slot offers no menu. */
  readonly onMenu?: (item: InventoryItem, x: number, y: number) => void;
  /** Whether any item drags (the grid's); otherwise only one that is worn, to its equip slot. */
  readonly drag?: boolean;
  /** Its slot as a drop target, in the grid. */
  readonly drop?: DropTarget;
}) {
  const draggable = drag || ((item.stats?.wears ?? 0) > 0 && item.slot <= 100);
  const tip = itemTooltip(item);
  const mk = mkNumeral(item.mk);
  const [hovered, setHovered] = useState<DOMRect | null>(null);
  const classes = ['inventory-slot', 'filled'];
  if (onPick !== null) {
    classes.push('pickable');
  }

  if (picked) {
    classes.push('picked');
  }

  if (selected) {
    classes.push('selected');
  }

  if (drop?.over === true) {
    classes.push('drop');
  }

  return (
    <div
      className={classes.join(' ')}
      data-testid={`inventory-slot-${String(item.slot)}`}
      data-item={item.item}
      aria-label={tip}
      role={drop === undefined ? undefined : 'option'}
      aria-selected={drop === undefined ? undefined : selected}
      draggable={draggable}
      onDragStart={
        draggable
          ? (e) => {
              e.dataTransfer.setData(ITEM_DRAG_TYPE, item.id);
              e.dataTransfer.effectAllowed = 'move';
              setHovered(null);
            }
          : undefined
      }
      {...dropHandlers(drop)}
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
      onDoubleClick={
        onActivate === undefined
          ? undefined
          : () => {
              onActivate(item.id);
            }
      }
      onContextMenu={
        onMenu === undefined
          ? undefined
          : (e) => {
              e.preventDefault();
              setHovered(null);
              onMenu(item, e.clientX, e.clientY);
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
      {item.count > 1 ? <span className="inventory-count">{slotCount(item.count)}</span> : null}
      {hovered === null ? null : <ItemTip text={tip} anchor={hovered} />}
    </div>
  );
}

const PROMPT_VERBS = { split: 'Split', unpack: 'Unpack', discard: 'Throw away' } as const;

/**
 * The count prompt under the grid (client.md C43): how many to split off, to unpack, or to throw away — and, for a throw-away, its confirmation, since
 * what is thrown away is destroyed. Enter confirms, Escape cancels (before the window's own Escape, which would close it).
 */
function CountPrompt({
  prompt,
  item,
  free,
}: {
  readonly prompt: ItemPrompt;
  readonly item: InventoryItem;
  /** The grid's free slots: an unpack takes no more than they hold. */
  readonly free: number;
}) {
  const range = promptRange(prompt.mode, item, free);
  const [typed, setTyped] = useState(String(range.start));
  const count = promptCount(typed, range);
  const cancel = (): void => {
    useItems.getState().openPrompt(null);
  };

  // Nothing left to ask — the stack went down to one, or the slots filled, while it was open: it closes rather than send a count the server refuses.
  const empty = !range.fixed && range.max < range.min;
  useEffect(() => {
    if (empty) {
      useItems.getState().openPrompt(null);
    }
  }, [empty]);
  if (empty) {
    return null;
  }

  const confirm = (): void => {
    const market = useMarket.getState();
    if (prompt.mode === 'split') {
      market.move(item.id, prompt.slot, count);
    } else if (prompt.mode === 'unpack') {
      market.unpack(item.id, count);
    } else {
      market.discard(item.id, count);
    }

    cancel();
  };

  const mk = mkNumeral(item.mk);
  const name = mk === '' ? item.name : `${item.name} Mk ${mk}`;
  const what =
    prompt.mode === 'discard'
      ? range.fixed
        ? `Throw away ${item.count > 1 ? `the crate of ${String(item.count)} ` : ''}${name}? It is destroyed for good.`
        : `Throw away how many of ${name}? They are destroyed for good.`
      : prompt.mode === 'split'
        ? `Split how many of ${name} into ${prompt.slot === 0 ? 'the first free slot' : `slot ${String(prompt.slot)}`}?`
        : `Unpack how many of the ${String(item.count)} ${name}?`;
  return (
    <div
      className={prompt.mode === 'discard' ? 'inventory-prompt danger' : 'inventory-prompt'}
      role="group"
      aria-label={PROMPT_VERBS[prompt.mode]}
      data-keep-enter
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          confirm();
        } else if (e.key === 'Escape') {
          e.preventDefault();
          e.stopPropagation();
          cancel();
        }
      }}
    >
      <div className="inventory-prompt-text">{what}</div>
      {range.fixed ? null : (
        <div className="inventory-prompt-count">
          <input
            type="range"
            min={range.min}
            max={range.max}
            value={count}
            aria-label="How many"
            onChange={(e) => {
              setTyped(e.target.value);
            }}
          />
          <input
            type="number"
            min={range.min}
            max={range.max}
            value={typed}
            aria-label="How many, typed"
            autoFocus
            onChange={(e) => {
              setTyped(e.target.value);
            }}
          />
        </div>
      )}
      <div className="inventory-prompt-buttons">
        <button
          type="button"
          className={prompt.mode === 'discard' ? 'danger' : undefined}
          autoFocus={range.fixed}
          onClick={confirm}
        >
          {PROMPT_VERBS[prompt.mode]}
          {range.fixed ? '' : ` ${count.toLocaleString('en-US')}`}
        </button>
        <button type="button" onClick={cancel}>
          Cancel
        </button>
      </div>
    </div>
  );
}

/** The preview panel: the selected item turning, its name, and every stat it has (client.md C43); the bazaar's board shows the same. */
export function ItemDetails({ item }: { readonly item: InventoryItem | undefined }) {
  if (item === undefined) {
    return (
      <aside className="inventory-details" style={{ width: PREVIEW_PANEL_W }} aria-label="Item preview">
        <div className="hint">Select an item to see it here.</div>
      </aside>
    );
  }

  const mk = mkNumeral(item.mk);
  return (
    <aside className="inventory-details" style={{ width: PREVIEW_PANEL_W }} aria-label="Item preview">
      <div className="inventory-details-name">
        {item.name}
        {mk === '' ? null : <span className={`inventory-mk inline mk${String(item.mk)}`}>{mk}</span>}
      </div>
      <ItemPreview asset={item.item} mk={item.mk} />
      <dl className="inventory-details-stats">
        {previewLines(item).map((line) => (
          <div key={line.label} className="inventory-details-line">
            <dt>{line.label}</dt>
            <dd>{line.value}</dd>
          </div>
        ))}
      </dl>
    </aside>
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
