import { formatCredits } from '../data/wallet';
import { useMissions } from '../state/mission-store';
import { useWallet } from '../state/wallet-store';

/** The item grid's slots: empty until items exist (economy-foundation.md — items and the bazaar come later). */
export const INVENTORY_SLOTS = 30;

/** How many of the wallet's recent transfers the window lists. */
const RECENT_SHOWN = 6;

const KIND_NAMES: Readonly<Record<string, string>> = {
  grant: 'Starting credits',
  mission: 'Mission reward',
  tip: 'Tip',
};

/**
 * The inventory (economy-foundation.md E0-10): the character's items — none yet, so an empty grid — and its credits at the
 * top right, with the last few transfers under the grid. Toggled by <kbd>I</kbd> and by the bottom-right toolbar.
 *
 * The balance is the wallet as last read (`WalletReader`), which re-reads it whenever a transfer touches this character.
 */
export function InventoryWindow() {
  const open = useWallet((s) => s.inventoryOpen);
  const wallet = useWallet((s) => s.wallet);
  const close = useWallet((s) => s.closeInventory);
  const journalOpen = useMissions((s) => s.journalOpen);
  if (!open) {
    return null;
  }

  const recent = wallet?.transfers.slice(0, RECENT_SHOWN) ?? [];
  return (
    <div
      className={journalOpen ? 'panel inventory beside-journal' : 'panel inventory'}
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
        {Array.from({ length: INVENTORY_SLOTS }, (_, i) => (
          <div key={i} className="inventory-slot" />
        ))}
      </div>
      <div className="hint">No items yet.</div>
      {recent.length > 0 ? (
        <div className="inventory-history">
          {recent.map((t) => (
            <div className="inventory-transfer" key={t.seq}>
              <span className="inventory-kind">
                {KIND_NAMES[t.kind] ?? t.kind}
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
