import { useEffect, useState } from 'react';
import { canAnswer, hordeLine, isOver } from '../data/horde';
import { useHorde } from '../state/horde-store';

/** How long a finished horde's line stays, ms. */
const OVER_LINGER_MS = 20_000;

/**
 * The horde's line at the top of the screen (horde.md § 6): "Raid on Whitestone: help 63 % — [Answer] [Mute]". Answer commits this character (the server
 * may say enough); Mute hides the line for this event, in this browser only. A countdown counts itself between statuses, which come on change only.
 */
export function HordeBar() {
  const status = useHorde((s) => s.status);
  const rally = useHorde((s) => s.rally);
  const muted = useHorde((s) => s.mutedEvent);
  const [now, setNow] = useState(() => performance.now());
  useEffect(() => {
    const timer = window.setInterval(() => {
      setNow(performance.now());
    }, 1000);
    return () => {
      window.clearInterval(timer);
    };
  }, []);

  if (status === null || muted === status.event || (isOver(status.phase) && now - status.heardAtMs > OVER_LINGER_MS)) {
    return null;
  }

  const line = hordeLine(status, now);
  if (line === '') {
    return null;
  }

  const committed = rally !== null && rally.event === status.event && (rally.state === 1 || rally.state === 2);
  return (
    <div
      className={isOver(status.phase) ? 'horde-bar over' : 'horde-bar'}
      role="status"
      aria-live="polite"
      data-testid="horde-bar"
    >
      <span className="horde-line">{line}</span>
      {committed ? <span className="horde-committed">{rally.state === 2 ? 'Defending' : 'On your way'}</span> : null}
      {canAnswer(status, rally) ? (
        <button
          type="button"
          onClick={() => {
            useHorde.getState().answer();
          }}
        >
          Answer
        </button>
      ) : null}
      {isOver(status.phase) ? null : (
        <button
          type="button"
          className="quiet"
          onClick={() => {
            useHorde.getState().mute();
          }}
        >
          Mute
        </button>
      )}
    </div>
  );
}
