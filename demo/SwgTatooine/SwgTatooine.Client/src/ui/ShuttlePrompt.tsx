import { useEffect, useState } from 'react';
import { REASON_NO_ANSWER, REASON_NOT_SENT, travelIsUnderway, type TravelState } from '../app/travel';
import { CITIES } from '../data/world-data';
import { useUi } from '../state/ui-store';

/**
 * The shuttleport: where to buy a ticket, and the journey once one is bought.
 *
 * <b>A ticket is bought at the terminal</b>: standing at the port's ticket terminal, a long press on it opens its context
 * menu, the destinations (`ContextMenu.tsx`, opened by `ClientApp`); a pick queues for the next shuttle (`BoardShuttle`),
 * which is drawn flying in and landing on the pad on the server's schedule. This panel only says how.
 */
export function ShuttlePrompt() {
  const shuttleport = useUi((s) => s.shuttleport);
  const travel = useUi((s) => s.travel);

  if (shuttleport === null && travel.phase === 'idle') {
    return null;
  }

  const here = shuttleport === null ? null : (CITIES[shuttleport.city]?.name ?? `city ${shuttleport.city}`);
  const underway = travelIsUnderway(travel);
  return (
    <>
      <div className="panel shuttle">
        <div className="hud-title">{here === null ? 'Shuttle' : `Shuttleport · ${here}`}</div>
        <TravelLine travel={travel} />
        {shuttleport !== null && !underway ? (
          <div className="hint">
            {shuttleport.atTerminal
              ? 'Ticket terminal — hold-click it to choose a destination.'
              : 'Get closer to the terminal to buy a ticket...'}
          </div>
        ) : null}
      </div>
    </>
  );
}

function cityName(index: number): string {
  return CITIES[index]?.name ?? `city ${index}`;
}

function refusalText(reason: number, city: number): string {
  switch (reason) {
    case REASON_NOT_SENT:
      return 'That ask was not sent — you are clicking faster than the server accepts. Try again.';
    case REASON_NO_ANSWER:
      return `No answer about ${cityName(city)}. The request may not have reached the server.`;
    case 131:
      return 'You are no longer standing at the shuttleport. Walk back to it and ask again.';
    case 132:
      return `${cityName(city)} is not somewhere this port flies to.`;
    default:
      return `The server refused the flight to ${cityName(city)} (reason ${reason}).`;
  }
}

function TravelLine({ travel }: { readonly travel: TravelState }) {
  const elapsed = useElapsedSeconds(travel.phase === 'queued' ? travel.sinceMs : null);
  switch (travel.phase) {
    case 'idle':
      return null;
    case 'asking':
      return <div className="travel asking">Asking for a seat to {cityName(travel.city)}…</div>;
    case 'queued':
      return (
        <div className="travel queued">
          <b>Ticket to {cityName(travel.city)}.</b> Waiting {elapsed} s for the shuttle to land. Stay at the port.
        </div>
      );
    case 'refused':
      return <div className="travel refused">{refusalText(travel.reason, travel.city)}</div>;
    case 'boarding':
      return <div className="travel asking">Boarding the shuttle to {cityName(travel.city)}…</div>;
    case 'done':
      return null;
    case 'arrived':
      return <div className="travel arrived">Arrived at {cityName(travel.city)}.</div>;
    default:
      return (
        <div className="travel stranded">
          No shuttle came for {cityName(travel.city)}, and the queue has closed. Buy another ticket to re-join it.
        </div>
      );
  }
}

function useElapsedSeconds(sinceMs: number | null): number {
  const [nowMs, setNowMs] = useState(() => performance.now());
  useEffect(() => {
    if (sinceMs === null) {
      return undefined;
    }

    const timer = window.setInterval(() => {
      setNowMs(performance.now());
    }, 1000);
    return () => {
      window.clearInterval(timer);
    };
  }, [sinceMs]);

  return sinceMs === null ? 0 : Math.max(0, Math.floor((nowMs - sinceMs) / 1000));
}
