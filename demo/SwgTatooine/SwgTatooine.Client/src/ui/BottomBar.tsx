import { useEffect } from 'react';
import { panelKey } from '../app/panel-keys';
import { useMissions } from '../state/mission-store';
import { useSessionStore } from '../state/session-store';
import { useUi } from '../state/ui-store';
import { useWallet } from '../state/wallet-store';

/**
 * The bottom-right toolbar (missions.md § 5): the mission journal's and the inventory's buttons, under the waypoint pane,
 * and above them the word that a mission was completed. A logged-in player's only — the god camera holds no missions and
 * no wallet.
 *
 * <b>And their keys</b> (Loïc, 2026-10-26): <kbd>J</kbd> toggles the journal, <kbd>I</kbd> the inventory — not while
 * typing, not with a modifier held, and not while a context menu is open, whose keys are its own.
 */
export function BottomBar() {
  const playing = useSessionStore((s) => s.grant !== null);
  const open = useMissions((s) => s.journalOpen);
  const held = useMissions((s) => s.journal.length);
  const toggle = useMissions((s) => s.toggleJournal);
  const completed = useMissions((s) => s.completed);
  const inventoryOpen = useWallet((s) => s.inventoryOpen);
  const toggleInventory = useWallet((s) => s.toggleInventory);

  useEffect(() => {
    if (!playing) {
      return undefined;
    }

    const onKey = (e: KeyboardEvent): void => {
      const panel = panelKey(e);
      if (panel === null || useUi.getState().contextMenu !== null) {
        return;
      }

      e.preventDefault();
      if (panel === 'journal') {
        useMissions.getState().toggleJournal();
      } else {
        useWallet.getState().toggleInventory();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, [playing]);

  if (!playing) {
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
        <button
          type="button"
          className={open ? 'active' : ''}
          onClick={toggle}
          title="Mission journal (J)"
          aria-pressed={open}
        >
          <span aria-hidden="true">📜</span> Missions{held > 0 ? ` (${held})` : ''}
        </button>
        <button
          type="button"
          className={inventoryOpen ? 'active' : ''}
          onClick={toggleInventory}
          title="Inventory (I)"
          aria-pressed={inventoryOpen}
        >
          <span aria-hidden="true">🎒</span> Inventory
        </button>
      </div>
    </>
  );
}
