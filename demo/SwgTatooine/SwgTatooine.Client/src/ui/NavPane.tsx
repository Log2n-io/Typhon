import { useUi, type NavRow } from '../state/ui-store';
import { useWaypoints } from '../state/waypoints-store';
import { useWindows } from '../state/windows-store';

/** The key a row is shown or left out by in the pane's filter (client.md C38): a city structure by its kind, the rest by their group. */
export function navFilterKey(row: Pick<NavRow, 'group' | 'kind'>): string {
  return row.group === 'city' ? `city:${String(row.kind)}` : row.group;
}

/**
 * The waypoint pane (client.md C38): the nearest shuttleport, bazaar and mission terminal, a row for each mission held, on its lair, and the player's
 * own waypoints, the active one first — name, distance and an arrow, each in its kind's colour or the one its owner chose. Up is straight ahead, down
 * behind, left and right as the camera sees them (SWG's waypoint list, after Loïc's reference). What it leaves out is chosen in the Waypoints window,
 * which its title opens.
 *
 * <b>The arrows are turned by the frame loop</b> (`ClientApp.paintNav`), not by React: they follow the camera every frame, and a React render per
 * frame for a few rotations would be the wrong tool. React lays the rows out when the scan changes them, five times a second at most.
 */
export function NavPane() {
  const rows = useUi((s) => s.nav);
  const scanMs = useUi((s) => s.navScanMs);
  const scanned = useUi((s) => s.navScanned);
  const walkTo = useUi((s) => s.walkTo);
  const debug = useWindows((s) => s.layout.debug);
  const hidden = useWindows((s) => s.layout.navHidden);
  const toggleWaypoints = useWaypoints((s) => s.toggle);
  if (rows.length === 0) {
    return null;
  }

  const shown = rows.filter((row) => !hidden.includes(navFilterKey(row)));
  return (
    <div className="panel nav">
      <button
        type="button"
        className="nav-head"
        onClick={toggleWaypoints}
        title="Your waypoints, and what this pane shows"
      >
        Waypoints ▸
      </button>
      {shown.map((row) => (
        <div
          className={row.active ? 'nav-row nav-active' : 'nav-row'}
          key={row.kind}
          role="button"
          title={`Walk to ${row.label}`}
          style={{ color: row.color }}
          onClick={() => {
            if (Number.isFinite(row.distanceM)) {
              walkTo(row.x, row.z);
            }
          }}
        >
          <span className="nav-label">
            {row.active ? '▶ ' : ''}
            {row.label}
          </span>
          <span className="nav-distance">{Number.isFinite(row.distanceM) ? formatDistance(row.distanceM) : '—'}</span>
          <span className="nav-arrow" data-nav-kind={row.kind} aria-hidden="true">
            ↑
          </span>
        </div>
      ))}
      {/* A measurement, so the debug layer's (C36). */}
      {debug ? (
        <div className="nav-foot" title="Time the client spent finding these, from the structures it already holds">
          {`scanned ${scanned} held structures in ${scanMs < 0.1 ? '< 0.1' : scanMs.toFixed(2)} ms`}
        </div>
      ) : null}
    </div>
  );
}

/** Metres under a kilometre, kilometres with one decimal past it. */
export function formatDistance(m: number): string {
  return m < 1000 ? `${Math.round(m)}m` : `${(m / 1000).toFixed(1)}km`;
}
