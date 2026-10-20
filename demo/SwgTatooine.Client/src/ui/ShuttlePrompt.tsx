import { CITIES } from '../data/world-data';
import { useUi } from '../state/ui-store';

/**
 * The shuttleport prompt: where to fly, shown only while the avatar is standing at a port.
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
 * <b>Nothing here pretends the ask was granted.</b> The server refuses a destination it does not like with an `ACKS`
 * reason, and even an accepted ask does nothing on the asking tick: the avatar queues, and the flight happens on the
 * port's next landing. The journey is visible when the body arrives somewhere else, which is the server's statement
 * rather than this component's.
 */
export function ShuttlePrompt() {
  const shuttleport = useUi((s) => s.shuttleport);
  const boardShuttle = useUi((s) => s.boardShuttle);
  if (shuttleport === null) {
    return null;
  }

  const here = CITIES[shuttleport.city]?.name ?? `city ${shuttleport.city}`;
  return (
    <div className="panel shuttle">
      <div className="hud-title">Shuttleport · {here}</div>
      <div className="hint">
        {shuttleport.distanceM} m away. The shuttle leaves on its own schedule — you queue, and it carries you when it
        lands.
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
    </div>
  );
}
