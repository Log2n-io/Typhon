import { create } from 'zustand';
import { lootLines, type InventorySnapshot } from '../data/items';
import { useChat } from './chat-store';

/**
 * The items on screen (inventory-loot.md § 6): the inventory as last read, and the catalogue's item icons.
 *
 * <b>Its own store</b>, for the wallet store's reason: the inventory changes at the world's pace, and the frame loop
 * publishes it here only when a new answer arrived. The icons are set once per asset mount.
 */
export interface ItemsState {
  /** The inventory as last read, or `null` before the first answer. */
  readonly snapshot: InventorySnapshot | null;
  /** Asset id → the URL of its 32 px icon; empty without a catalogue (no `--assets`). */
  readonly icons: Readonly<Record<string, string>>;

  /** A new read: each drop and each loss since the last read of this session is one chat line (`lootLines`). */
  readonly setSnapshot: (snapshot: InventorySnapshot) => void;
  readonly setIcons: (icons: Readonly<Record<string, string>>) => void;
  /** Forgets the inventory without announcing anything: a new session. The icons are the catalogue's and stay. */
  readonly reset: () => void;
}

export const useItems = create<ItemsState>((set, get) => ({
  snapshot: null,
  icons: {},

  setSnapshot: (snapshot) => {
    const chat = useChat.getState();
    for (const line of lootLines(get().snapshot, snapshot)) {
      chat.notice(line);
    }

    set({ snapshot });
  },
  setIcons: (icons) => {
    set({ icons });
  },
  reset: () => {
    set({ snapshot: null });
  },
}));
