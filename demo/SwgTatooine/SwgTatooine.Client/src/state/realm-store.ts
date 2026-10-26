import { create } from 'zustand';
import type { RealmInventory } from '../data/realm-inventory';

/**
 * The live realm inventory, polled about once a second (CLI3D-11).
 *
 * <b>Its own store rather than a field on `ui-store`, and the reason is a regression this repo has already paid for
 * once.</b> `Toolbar` subscribes to the whole UI store (`const ui = useUi()`), so replacing that store's state once a
 * second re-renders the toolbar once a second — the radius slider under the user's thumb included. The comment at the
 * top of `Toolbar` says exactly that, about the stats store, and the fix there was to select narrowly. A second
 * high-frequency field in the store nobody selects narrowly from would have reintroduced it through the other door.
 *
 * Only `RealmPanel` reads this, and it is the only thing that re-renders when a poll lands.
 */
export interface RealmState {
  /** The last document read, or `null` from a source that does not report one. */
  readonly inventory: RealmInventory | null;
  /**
   * Whether the panel is expanded.
   *
   * <b>Held here rather than left to the `<details>` element.</b> The panel renders nothing when there is no inventory,
   * so one failed poll unmounts it — and `open` is uncontrolled DOM state that does not survive that. A viewer who had
   * the panel open would find it closed after any hiccup, which reads as the panel having done it by itself.
   */
  readonly open: boolean;

  readonly setInventory: (inventory: RealmInventory | null) => void;
  readonly setOpen: (open: boolean) => void;
}

export const useRealms = create<RealmState>()((set) => ({
  inventory: null,
  open: false,
  setInventory: (inventory) => {
    set({ inventory });
  },
  setOpen: (open) => {
    set({ open });
  },
}));
