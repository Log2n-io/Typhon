import { useEffect, useState } from 'react';
import { REASON_NO_ANSWER, REASON_NOT_SENT, travelIsUnderway, type TravelState } from '../app/travel';
import { CITIES } from '../data/world-data';
import { useUi } from '../state/ui-store';

/**
 * The shuttleport prompt: where to fly, and what is happening to the request.
 *
 * <b>Parity is the reason this exists at all.</b> Shuttle travel was server-side AI — the simulated crowd flew and no
 * client could — so a bot's routing had three answers of which one was unreachable for every client, and a human
 * watching a crowd board could not follow it. The rule the design states is that a capability lands as a browser
 * affordance and a bot behaviour in the same change, because that is what gives it an A/B: the same journey made by a
 * person and by a bot against one server.
 *
 * <b>It appears from the replicated view, not from the map data.</b> The client ships the cities but not the ports —
 * which building of a city is its shuttleport is a draw the world generator made — so the prompt appears because the
 * server sent a structure whose `kind` says shuttleport and whose `region` says which city it serves. That is the same
 * mechanism the bot brain uses to find one, deliberately.
 *
 * <b>Every line below is something the server said.</b> The first version of this panel offered destinations and then
 * went silent: clicking one sent a command whose acceptance, queue and flight were all invisible, so the two available
 * readings were "it is broken" and "wait longer". What is reported now is the ask, the queue with the time spent in it,
 * the refusal and its reason, and the two ways a queue can end — each entered because something observable said so.
 * The one thing deliberately not shown is an estimate of when the shuttle will come: its schedule is not on the wire,
 * and a countdown this client invented would be the most convincing lie on screen.
 */
export function ShuttlePrompt() {
  const shuttleport = useUi((s) => s.shuttleport);
  const travel = useUi((s) => s.travel);
  const boardShuttle = useUi((s) => s.boardShuttle);

  // Shown while a journey is underway even after the body has left the port's radius, because a queue outlives the
  // prompt that started it: the server scatters an arrival across the destination's disc, and a panel that vanished on
  // departure would take the only report of the flight with it.
  if (shuttleport === null && travel.phase === 'idle') {
    return null;
  }

  const here = shuttleport === null ? null : (CITIES[shuttleport.city]?.name ?? `city ${shuttleport.city}`);
  return (
    <div className="panel shuttle">
      <div className="hud-title">{here === null ? 'Shuttle' : `Shuttleport · ${here}`}</div>
      <TravelLine travel={travel} />
      {shuttleport !== null && !travelIsUnderway(travel) ? (
        <>
          <div className="hint">
            {shuttleport.distanceM} m away. The shuttle leaves on its own schedule — you queue, and it carries you when
            it lands.
          </div>
          <div className="shuttle-destinations">
            {CITIES.map((city, index) =>
              index === shuttleport.city ? null : (
                <button
                  key={city.name}
                  type="button"
                  onClick={() => {
                    boardShuttle(index);
                  }}
                  title={`Queue for the next shuttle to ${city.name}`}
                >
                  {city.name}
                </button>
              ),
            )}
          </div>
        </>
      ) : null}
    </div>
  );
}

/** A destination's name as the panel says it, falling back to its index when the map data has no such city. */
function cityName(index: number): string {
  return CITIES[index]?.name ?? `city ${index}`;
}

/** Why the server would not fly this body, in the words a person can act on. */
function refusalText(reason: number, city: number): string {
  switch (reason) {
    case REASON_NOT_SENT:
      return 'That ask was not sent — you are clicking faster than the server accepts. Try again.';
    case REASON_NO_ANSWER:
      return `No answer about ${cityName(city)}. The request may not have reached the server.`;
    case 131:
      // ShuttleNotAtPort. The prompt only appears within 20 m of a port, so reaching this means the body moved between
      // the click and the server reading it — which is exactly what walking away does.
      return 'You are no longer standing at the shuttleport. Walk back to it and ask again.';
    case 132:
      // ShuttleBadDestination: a city that does not exist, or the one the body is already in.
      return `${cityName(city)} is not somewhere this port flies to.`;
    default:
      return `The server refused the flight to ${cityName(city)} (reason ${reason}).`;
  }
}

/**
 * The one line that says what is happening, and the only part of this panel that changes while a person waits.
 *
 * The queue's figure is seconds spent waiting rather than seconds remaining, which is the honest direction: the port's
 * schedule is the server's and nothing on the wire describes it.
 */
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
          <b>In the queue for {cityName(travel.city)}.</b> Waiting {elapsed} s — the shuttle lands on its own schedule.
          Stay at the port.
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
          No shuttle came for {cityName(travel.city)}, and the queue has closed. Ask again to re-join it.
        </div>
      );
  }
}

/**
 * Whole seconds since an instant, ticking once a second, or 0 when there is no instant.
 *
 * <b>A second's interval and not the render loop.</b> The waiting figure is the only thing on this panel that changes
 * on its own, and re-rendering it at frame rate to move a digit once a second is sixty times the work for the same
 * pixels. The interval is dropped and restarted when the instant changes, so a second journey counts from its own
 * start rather than inheriting the first one's clock.
 */
function useElapsedSeconds(sinceMs: number | null): number {
  // The CLOCK is the state and the figure is derived from it during render. Holding the figure instead would mean
  // seeding it from inside the effect, which is a setState in an effect body: a cascading render, and the lint rule
  // that forbids it is right. This way the only thing the interval does is move the clock forward.
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

  // Clamped at zero: the clock is from the last tick, so the first render of a new wait reads a moment before it began.
  return sinceMs === null ? 0 : Math.max(0, Math.floor((nowMs - sinceMs) / 1000));
}
