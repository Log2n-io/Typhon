import { useUi } from '../state/ui-store';

/**
 * The nearest shuttleport, bazaar and mission terminal: name, distance and an arrow — up is straight ahead, down behind,
 * left and right as the camera sees them (SWG's waypoint list, after Loïc's reference).
 *
 * <b>The arrows are turned by the frame loop</b> (`ClientApp.paintNav`), not by React: they follow the camera every
 * frame, and a React render per frame for three rotations would be the wrong tool. React lays the rows out when the
 * scan changes them, five times a second at most.
 */
export function NavPane() {
  const rows = useUi((s) => s.nav);
  const scanMs = useUi((s) => s.navScanMs);
  const scanned = useUi((s) => s.navScanned);
  const walkTo = useUi((s) => s.walkTo);
  if (rows.length === 0) {
    return null;
  }

  return (
    <div className="panel nav">
      {rows.map((row) => (
        <div
          className="nav-row"
          key={row.kind}
          role="button"
          title={`Walk to the ${row.label}`}
          onClick={() => {
            if (Number.isFinite(row.distanceM)) {
              walkTo(row.x, row.z);
            }
          }}
        >
          <span className="nav-label">{row.label}</span>
          <span className="nav-distance">{Number.isFinite(row.distanceM) ? formatDistance(row.distanceM) : '—'}</span>
          <span className="nav-arrow" data-nav-kind={row.kind} aria-hidden="true">
            ↑
          </span>
        </div>
      ))}
      <div className="nav-foot" title="Time the client spent finding these, from the structures it already holds">
        {`scanned ${scanned} held structures in ${scanMs < 0.1 ? '< 0.1' : scanMs.toFixed(2)} ms`}
      </div>
    </div>
  );
}

/** Metres under a kilometre, kilometres with one decimal past it. */
export function formatDistance(m: number): string {
  return m < 1000 ? `${Math.round(m)}m` : `${(m / 1000).toFixed(1)}km`;
}
