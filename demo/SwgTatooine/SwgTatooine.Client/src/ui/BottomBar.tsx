import { useEffect } from 'react';
import { panelKey } from '../app/panel-keys';
import { useItems } from '../state/items-store';
import { useMarket } from '../state/market-store';
import { useMissions } from '../state/mission-store';
import { useSessionStore } from '../state/session-store';
import { useUi } from '../state/ui-store';
import { useWallet } from '../state/wallet-store';

/**
 * The bottom-right toolbar (missions.md § 5): the mission journal's and the inventory's buttons, under the waypoint pane,
 * and above them the word that a mission was completed. A logged-in player's only — the god camera holds no missions and
 * no wallet — except the inventory's against the mock, whose own character has one (`data/mock/inventory.ts`).
 *
 * <b>And their keys</b> (Loïc, 2026-10-26): <kbd>J</kbd> toggles the journal, <kbd>I</kbd> the inventory, <kbd>B</kbd> the
 * bazaar (bazaar.md § 6: browsing and cancelling work anywhere; the rest at a bazaar terminal) — not while typing, not with
 * a modifier held, and not while a context menu is open, whose keys are its own.
 */
export function BottomBar() {
  const playing = useSessionStore((s) => s.grant !== null);
  // The mock answers an inventory with no login, so the window can be seen without a server.
  const hasItems = useItems((s) => s.snapshot !== null);
  const shown = playing || hasItems;
  const open = useMissions((s) => s.journalOpen);
  const held = useMissions((s) => s.journal.length);
  const toggle = useMissions((s) => s.toggleJournal);
  const completed = useMissions((s) => s.completed);
  const inventoryOpen = useWallet((s) => s.inventoryOpen);
  const toggleInventory = useWallet((s) => s.toggleInventory);
  const bazaarOpen = useMarket((s) => s.open);
  const toggleBazaar = useMarket((s) => s.toggle);
  const reserved = useMarket((s) => s.mine?.reserved.length ?? 0);

  useEffect(() => {
    if (!shown) {
      return undefined;
    }

    const onKey = (e: KeyboardEvent): void => {
      const panel = panelKey(e);
      if (panel === null || useUi.getState().contextMenu !== null) {
        return;
      }

      if (panel === 'journal' && !playing) {
        return;
      }

      e.preventDefault();
      if (panel === 'journal') {
        useMissions.getState().toggleJournal();
      } else if (panel === 'bazaar') {
        useMarket.getState().toggle();
      } else {
        useWallet.getState().toggleInventory();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, [shown, playing]);

  if (!shown) {
    return null;
  }

  return (
    <>
      {completed !== null ? (
        <div className="panel mission-toast" role="status">
          {completed.text}
        </div>
      ) : null}
      <div className="panel bottombar">
        {playing ? (
          <button
            type="button"
            className={open ? 'active' : ''}
            onClick={toggle}
            title="Mission journal (J)"
            aria-pressed={open}
          >
            <span aria-hidden="true">📜</span> Missions{held > 0 ? ` (${held})` : ''}
          </button>
        ) : null}
        <button
          type="button"
          className={inventoryOpen ? 'active' : ''}
          onClick={toggleInventory}
          title="Inventory (I)"
          aria-pressed={inventoryOpen}
        >
          <span aria-hidden="true">🎒</span> Inventory
        </button>
        <button
          type="button"
          className={bazaarOpen ? 'active' : ''}
          onClick={toggleBazaar}
          title="Bazaar (B)"
          aria-pressed={bazaarOpen}
        >
          <span aria-hidden="true">⚖</span> Bazaar{reserved > 0 ? ` (${String(reserved)})` : ''}
        </button>
      </div>
    </>
  );
}
