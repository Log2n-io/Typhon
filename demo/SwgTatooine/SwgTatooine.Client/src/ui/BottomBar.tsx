import { useMissions } from '../state/mission-store';
import { useSessionStore } from '../state/session-store';

/**
 * The bottom-right toolbar (missions.md § 5): the mission journal's button, under the waypoint pane, and above it the word
 * that a mission was completed. A logged-in player's only — the god camera holds no missions.
 */
export function BottomBar() {
  const playing = useSessionStore((s) => s.grant !== null);
  const open = useMissions((s) => s.journalOpen);
  const held = useMissions((s) => s.journal.length);
  const toggle = useMissions((s) => s.toggleJournal);
  const completed = useMissions((s) => s.completed);
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
          title="Mission journal"
          aria-pressed={open}
        >
          <span aria-hidden="true">📜</span> Missions{held > 0 ? ` (${held})` : ''}
        </button>
      </div>
    </>
  );
}
