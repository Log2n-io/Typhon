import { conClass, viewerClOf } from '../data/content-level';
import { formatMissionDistance, MAX_MISSIONS, missionTitle, OCTANT_NAMES } from '../data/missions';
import { useMissions } from '../state/mission-store';
import { useStats } from '../state/stats-store';

/**
 * The mission journal (missions.md § 5): the missions this character holds, each cancellable. Opened from the bottom-right
 * toolbar, or from a terminal's menu.
 *
 * <b>Nothing to hand in.</b> A mission ends where its lair is broken, paid there; the journal only says what is held and
 * where it was listed. Where the lair stands now is the waypoint pane's — it moves when somebody else breaks it, or when
 * its time runs out.
 */
export function MissionJournalPanel() {
  const open = useMissions((s) => s.journalOpen);
  const journal = useMissions((s) => s.journal);
  const notice = useMissions((s) => s.notice);
  const cancel = useMissions((s) => s.cancel);
  const cancelling = useMissions((s) => s.cancelling);
  const close = useMissions((s) => s.closeJournal);
  const viewerCl = useStats((s) => viewerClOf(s.stats?.vitals?.level));
  if (!open) {
    return null;
  }

  return (
    <div className="panel journal" role="dialog" aria-label="Mission journal">
      <div className="missions-head">
        <span className="hud-title">Mission journal</span>
        <span className="missions-held">
          {journal.length}/{MAX_MISSIONS}
        </span>
        <button type="button" className="missions-close" onClick={close} aria-label="Close">
          ✕
        </button>
      </div>
      {notice !== null ? <div className="missions-notice">{notice}</div> : null}
      {journal.length === 0 ? (
        <div className="hint">No missions held. Take one at a mission terminal.</div>
      ) : (
        journal.map((m) => (
          <div className="journal-row" key={m.id}>
            <div className="journal-main">
              <div className="journal-title">{missionTitle(m.name)}</div>
              <div className="journal-meta">
                <span className={conClass(m.level, viewerCl)}>Level {m.level}</span> · {m.reward} cr · listed ≈{' '}
                {formatMissionDistance(m.distanceM)} {OCTANT_NAMES[m.octant] ?? '?'} of its terminal
                {m.lair === null ? ' · lair moving…' : ''}
              </div>
            </div>
            <button
              type="button"
              disabled={cancelling.includes(m.id)}
              onClick={() => {
                cancel(m.id);
              }}
              title="Give this mission up: it and its lair are gone"
            >
              {cancelling.includes(m.id) ? 'Cancelling…' : 'Cancel'}
            </button>
          </div>
        ))
      )}
    </div>
  );
}
