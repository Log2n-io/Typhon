import { create } from 'zustand';
import { noticeLines, type PlayerNotice } from '../data/notices';
import { type WalletSnapshot } from '../data/wallet';
import { useChat } from './chat-store';
import { useMarket } from './market-store';

/**
 * The wallet on screen (economy-foundation.md E0-10): the balance as last read, whether the inventory window is open, and
 * the tip the app sends. The moves of credits and items are said in the chat from the server's notices
 * ({@link announceNotices}), never from a ledger read.
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

  /** A new read of the wallet. */
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

/** Says one frame's notices in the chat (`noticeLines`): a drop, a tip, a sale with its tax, a purchase, a vendor's payment. */
export function announceNotices(batch: readonly PlayerNotice[]): void {
  const market = useMarket.getState();
  const chat = useChat.getState();
  for (const line of noticeLines(batch, (amount, ref) => market.saleItem(amount, ref))) {
    chat.notice(line);
  }
}
