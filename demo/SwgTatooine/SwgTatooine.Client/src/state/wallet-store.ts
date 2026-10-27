import { create } from 'zustand';
import { marketWalletLine } from '../data/market';
import { formatCredits, type WalletSnapshot } from '../data/wallet';
import { useChat } from './chat-store';
import { useMarket } from './market-store';

/**
 * The wallet on screen (economy-foundation.md E0-10): the balance and recent transfers as last read, whether the inventory
 * window is open, and the tip the app sends.
 *
 * <b>Its own store</b>, for the mission store's reason: the wallet changes a few times a minute, at the world's pace, and
 * the frame loop publishes it here only when a new answer arrived. The tip is the app's to send (`ClientApp` registers it),
 * so this file never sees a socket.
 */

/** What the app sends for the panels. */
export interface WalletActions {
  /** `GiveCredits`; false when nothing left the client (the command's rate, or a session that may not). */
  readonly tip: (target: number, amount: number) => boolean;
}

export interface WalletState {
  /** The wallet as last read, or `null` before the first answer. */
  readonly wallet: WalletSnapshot | null;
  readonly inventoryOpen: boolean;
  readonly actions: WalletActions | null;

  /** A new read of the wallet: a tip given or received since the last one is said in the chat. */
  readonly setWallet: (wallet: WalletSnapshot) => void;
  /** Forgets the wallet without announcing anything: a new session. */
  readonly resetWallet: () => void;
  readonly toggleInventory: () => void;
  readonly closeInventory: () => void;
  readonly setActions: (actions: WalletActions | null) => void;
}

export const useWallet = create<WalletState>((set, get) => ({
  wallet: null,
  inventoryOpen: false,
  actions: null,

  setWallet: (wallet) => {
    // Announced only between two reads of this session: the first read's rows are history, not news.
    const before = get().wallet;
    if (before !== null) {
      const known = new Set(before.transfers.map((t) => t.seq));
      const fresh = wallet.transfers.filter((t) => !known.has(t.seq)).reverse();
      for (const t of fresh) {
        const who = t.other ?? 'somebody';
        if (t.kind === 'tip') {
          useChat
            .getState()
            .notice(
              t.amount > 0
                ? `${who} tipped you ${formatCredits(t.amount)}`
                : `You tipped ${who} ${formatCredits(-t.amount)}`,
            );
          continue;
        }

        // The bazaar's (bazaar.md § 6): a sale with the tax the same tick paid, a purchase, the vendor's payment.
        const tax =
          t.kind === 'sale' && t.amount > 0
            ? -(fresh.find((x) => x.kind === 'tax' && x.tick === t.tick)?.amount ?? 0)
            : 0;
        const what = t.kind === 'sale' ? useMarket.getState().saleItem(t.amount) : null;
        const line = marketWalletLine(t.kind, t.amount, t.other, tax, what);
        if (line !== null) {
          useChat.getState().notice(line);
        }
      }
    }

    set({ wallet });
  },
  resetWallet: () => {
    set({ wallet: null });
  },
  toggleInventory: () => {
    set({ inventoryOpen: !get().inventoryOpen });
  },
  closeInventory: () => {
    set({ inventoryOpen: false });
  },
  setActions: (actions) => {
    set({ actions });
  },
}));
