import { useStats } from '../state/stats-store';

/** The server's `Downed.KnockedOut`. */
const KNOCKED_OUT = 1;

/**
 * The avatar is down: knocked out, or dead and about to wake as a clone — and how long until it is up. Counted down from
 * the server's own tick (`upTick`), four times a second with the rest of the stats.
 */
export function DownNotice() {
  const down = useStats((s) => s.stats?.down ?? null);
  if (down === null) {
    return null;
  }

  const seconds = Math.ceil(down.seconds);
  const knocked = down.kind === KNOCKED_OUT;
  return (
    <div className="down-notice">
      <div className={knocked ? 'down-title knocked' : 'down-title'}>{knocked ? 'Knocked out' : 'You died'}</div>
      <div className="down-sub">
        {knocked
          ? `Back on your feet in ${String(seconds)} s`
          : `A clone wakes at the nearest shuttleport in ${String(seconds)} s`}
      </div>
    </div>
  );
}
