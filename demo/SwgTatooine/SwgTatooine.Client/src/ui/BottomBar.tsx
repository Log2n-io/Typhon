import { useEffect } from 'react';
import { panelKey } from '../app/panel-keys';
import { useChat } from '../state/chat-store';
import { useItems } from '../state/items-store';
import { useCharacter } from '../state/character-store';
import { useMap } from '../state/map-store';
import { useMissions } from '../state/mission-store';
import { useSessionStore } from '../state/session-store';
import { useCraft } from '../state/craft-store';
import { useUi } from '../state/ui-store';
import { useWallet } from '../state/wallet-store';
import { useWindows } from '../state/windows-store';

/**
 * The bottom-right toolbar (missions.md § 5): the mission journal's and the inventory's buttons, under the waypoint pane,
 * and above them the word that a mission was completed. A logged-in player's only — the god camera holds no missions and
 * no wallet — except the inventory's against the mock, whose own character has one (`data/mock/inventory.ts`).
 *
 * <b>And their keys</b> (Loïc, 2026-10-26): <kbd>J</kbd> toggles the journal, <kbd>I</kbd> the inventory, <kbd>K</kbd> the crafting panel at a
 * station only (the server refuses everything in it elsewhere), <kbd>M</kbd> the planet map (Loïc, 2026-11-02), <kbd>C</kbd> the character,
 * <kbd>L</kbd> its structures, <kbd>O</kbd> the options (client.md C36) — not while typing, not with a modifier held, and not while a context menu
 * is open, whose keys are its own. <b>No bazaar</b>: it is used at its terminal, from its menu, and closes when the body walks away (C40).
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
  const mapOpen = useMap((s) => s.open);
  const toggleMap = useMap((s) => s.toggle);
  const characterOpen = useCharacter((s) => s.open);
  const characterTab = useCharacter((s) => s.tab);
  const toggleCharacter = useCharacter((s) => s.toggle);
  const optionsOpen = useWindows((s) => s.optionsOpen);
  const toggleOptions = useWindows((s) => s.toggleOptions);

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
      } else if (panel === 'map') {
        useMap.getState().toggle();
      } else if (panel === 'installations') {
        // The Lots window is the character window's Structures tab (client.md C39).
        useCharacter.getState().toggle('structures');
      } else if (panel === 'character') {
        useCharacter.getState().toggle();
      } else if (panel === 'options') {
        useWindows.getState().toggleOptions();
      } else if (panel === 'craft') {
        const craft = useCraft.getState();
        if (craft.open || craft.atStation) {
          craft.toggle();
        } else {
          useChat.getState().notice('Crafting needs a crafting station: hold-click one, or press K beside it.');
        }
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
          className={mapOpen ? 'active' : ''}
          onClick={toggleMap}
          title="Planet map (M)"
          aria-pressed={mapOpen}
        >
          <span aria-hidden="true">🗺</span> Map
        </button>
        {playing ? (
          <>
            <button
              type="button"
              className={characterOpen && characterTab !== 'structures' ? 'active' : ''}
              onClick={() => {
                // From its Structures tab, the overview; else open or shut where it was.
                toggleCharacter(characterTab === 'structures' ? 'overview' : undefined);
              }}
              title="Character (C)"
              aria-pressed={characterOpen && characterTab !== 'structures'}
            >
              <span aria-hidden="true">👤</span> Character
            </button>
            <button
              type="button"
              className={characterOpen && characterTab === 'structures' ? 'active' : ''}
              onClick={() => {
                toggleCharacter('structures');
              }}
              title="Your structures (L)"
              aria-pressed={characterOpen && characterTab === 'structures'}
            >
              <span aria-hidden="true">⛏</span> Lots
            </button>
          </>
        ) : null}
        <button
          type="button"
          className={optionsOpen ? 'active' : ''}
          onClick={toggleOptions}
          title="Options (O)"
          aria-pressed={optionsOpen}
        >
          <span aria-hidden="true">⚙</span>
        </button>
      </div>
    </>
  );
}
