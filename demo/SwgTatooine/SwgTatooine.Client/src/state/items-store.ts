import { create } from 'zustand';
import type { Descriptor } from '../assets/descriptor';
import type { ItemPrompt } from '../data/inventory-grid';
import { lostLines, type InventorySnapshot } from '../data/items';
import { useChat } from './chat-store';

/** An item's 3D model, as the preview loads it: its GLB's URL and its descriptor (ASSET_FORMAT.md § 5.11, its Mk material). */
export interface ItemModel {
  readonly glb: string;
  readonly descriptor: Descriptor;
}

/** Finds an item's model by its asset id; `null` for one the catalogue does not hold. */
export type ItemModelSource = (asset: string) => Promise<ItemModel | null>;

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
  /** Where the preview finds a model: set at asset mount, `null` without a catalogue. */
  readonly models: ItemModelSource | null;
  /** The stack the window has selected (client.md C43), by id: what the preview shows. */
  readonly selected: string | null;
  /** The count prompt open on an item, or `null`. */
  readonly prompt: ItemPrompt | null;

  /** A new read: each loss since the last read of this session is one chat line (`lostLines`); a drop is the server's notice. */
  readonly setSnapshot: (snapshot: InventorySnapshot) => void;
  readonly setIcons: (icons: Readonly<Record<string, string>>) => void;
  readonly setModels: (models: ItemModelSource | null) => void;
  readonly select: (id: string | null) => void;
  /** Opens a count prompt, the item selected with it; `null` closes it. */
  readonly openPrompt: (prompt: ItemPrompt | null) => void;
  /** Forgets the inventory without announcing anything: a new session. The icons are the catalogue's and stay. */
  readonly reset: () => void;
}

export const useItems = create<ItemsState>((set, get) => ({
  snapshot: null,
  icons: {},
  models: null,
  selected: null,
  prompt: null,

  setSnapshot: (snapshot) => {
    const chat = useChat.getState();
    for (const line of lostLines(get().snapshot, snapshot)) {
      chat.notice(line, 'loot');
    }

    set({ snapshot });
  },
  setIcons: (icons) => {
    set({ icons });
  },
  setModels: (models) => {
    set({ models });
  },
  select: (selected) => {
    set({ selected });
  },
  openPrompt: (prompt) => {
    set(prompt === null ? { prompt } : { prompt, selected: prompt.item });
  },
  reset: () => {
    set({ snapshot: null, selected: null, prompt: null });
  },
}));
