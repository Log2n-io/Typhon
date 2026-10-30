import { CON_YELLOW_MAX, conClass, OFFER_BELOW_MAX, offerInReach, viewerClOf } from '../data/content-level';
import {
  formatMissionDistance,
  listedOffers,
  MAX_MISSIONS,
  missionTitle,
  OCTANT_NAMES,
  sortOffers,
  type OfferSort,
} from '../data/missions';
import { useMissions } from '../state/mission-store';
import { useStats } from '../state/stats-store';

/**
 * A mission terminal's list (missions.md § 5): opened from the terminal's context menu, closed by walking away from it.
 *
 * <b>The same list for everybody in the city, live.</b> The offers are replicated entities standing at the terminal, so a
 * row somebody else took leaves at once, and its replacement arrives with it — the list stays full however many people
 * are taking from it. A take that lost the race is told so ("Mission already taken") rather than finding out later.
 *
 * <b>Only what you would take</b> (Loïc, 2026-10-26): offers from five levels below your CL to three above, each level in its
 * con colour; "Show all" lists the rest, dimmed. Without a CL — the god camera — everything is listed.
 */
export function MissionTerminal() {
  const terminal = useMissions((s) => s.terminal);
  const offers = useMissions((s) => s.offers);
  const sort = useMissions((s) => s.sort);
  const descending = useMissions((s) => s.descending);
  const notice = useMissions((s) => s.notice);
  const asking = useMissions((s) => s.asking);
  const held = useMissions((s) => s.journal.length);
  const kindNames = useMissions((s) => s.kindNames);
  const sortBy = useMissions((s) => s.sortBy);
  const showAll = useMissions((s) => s.showAll);
  const toggleShowAll = useMissions((s) => s.toggleShowAll);
  const viewerCl = useStats((s) => viewerClOf(s.stats?.vitals?.level));
  const take = useMissions((s) => s.take);
  const close = useMissions((s) => s.closeTerminal);
  if (terminal === null) {
    return null;
  }

  const listed = listedOffers(offers, viewerCl, showAll);
  const rows = sortOffers(listed.rows, sort, descending);
  const full = held >= MAX_MISSIONS;
  const header = (by: OfferSort, label: string) => (
    <th>
      <button
        type="button"
        className={sort === by ? 'sorted' : ''}
        onClick={() => {
          sortBy(by);
        }}
        title={`Sort by ${label.toLowerCase()}`}
      >
        {label}
        {sort === by ? (descending ? ' ▾' : ' ▴') : ''}
      </button>
    </th>
  );

  return (
    <div className="panel missions" role="dialog" aria-label="Mission terminal">
      <div className="missions-head">
        <span className="hud-title">Mission terminal</span>
        <span className="missions-held">
          {held}/{MAX_MISSIONS} held
        </span>
        <button type="button" className="missions-close" onClick={close} aria-label="Close">
          ✕
        </button>
      </div>
      {notice !== null ? <div className="missions-notice">{notice}</div> : null}
      {full ? (
        <div className="hint">You hold {MAX_MISSIONS} missions — cancel one in the journal to take another.</div>
      ) : null}
      <table className="missions-table">
        <thead>
          <tr>
            <th>Mission</th>
            {header('level', 'Level')}
            {header('direction', 'Dir')}
            {header('distance', 'Distance')}
            {header('reward', 'Reward')}
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map((o) => (
            <tr key={o.serial} className={offerInReach(o.level, viewerCl) ? undefined : 'out-of-reach'}>
              <td>{missionTitle(kindNames[o.kind] ?? '')}</td>
              <td className={`num ${conClass(o.level, viewerCl) ?? ''}`}>{o.level}</td>
              <td>{OCTANT_NAMES[o.octant] ?? '?'}</td>
              <td className="num">≈ {formatMissionDistance(o.distanceM)}</td>
              <td className="num">{o.reward} cr</td>
              <td>
                <button
                  type="button"
                  disabled={full || asking !== null}
                  onClick={() => {
                    take(o);
                  }}
                >
                  {asking === o.serial ? '…' : 'Take'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length === 0 ? (
        <div className="hint">{listed.hidden > 0 ? 'Nothing here for your level.' : 'Nothing listed here yet.'}</div>
      ) : null}
      {viewerCl !== null && (showAll || listed.hidden > 0) ? (
        <div className="row missions-filter">
          <button type="button" className={showAll ? 'active' : ''} onClick={toggleShowAll}>
            {showAll ? 'Show mine' : `Show all (${String(listed.hidden)} more)`}
          </button>
          <span className="hint">
            {showAll
              ? 'Every offer, those out of your reach dimmed'
              : `CL ${String(Math.max(1, viewerCl - OFFER_BELOW_MAX))}–${String(viewerCl + CON_YELLOW_MAX)} shown`}
          </span>
        </div>
      ) : null}
    </div>
  );
}
