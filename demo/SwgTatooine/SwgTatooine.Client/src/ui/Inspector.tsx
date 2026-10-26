import { conColour } from '../data/content-level';
import { doorRealmOf } from '../data/realm-view';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

/** The viewer's combat level, for a level's con colour: none until the character's CL is replicated (character-level.md). */
const VIEWER_CL: number | null = null;

/** The picked entity, live: what the server replicated for it, and where it is going. */
export function Inspector() {
  const selectedNetId = useUi((s) => s.selectedNetId);
  const follow = useUi((s) => s.follow);
  const select = useUi((s) => s.select);
  const setFollow = useUi((s) => s.setFollow);
  const viewRealm = useUi((s) => s.viewRealm);
  const spectate = useUi((s) => s.spectate);
  const walkDoor = useUi((s) => s.walkDoor);
  const playing = useUi((s) => s.playing);
  const published = useStats((s) => s.stats?.inspection ?? null);
  const realm = useStats((s) => s.stats?.realm ?? null);
  const canViewRealm = useStats((s) => s.stats?.canViewRealm) ?? false;
  const canSpectate = useStats((s) => s.stats?.canSpectate) ?? false;
  const ridingNetId = useStats((s) => s.stats?.ridingNetId) ?? 0;
  if (selectedNetId === 0) {
    return null;
  }

  // Stats arrive four times a second: until then, what was published may describe the previous selection.
  const inspection = published?.netId === selectedNetId ? published : null;

  // A door, when the thing picked has one. `interior` rides in with the entity (`Structure.InteriorRealm`), so it is read
  // from the replicated fields by name rather than given a slot of its own in `Inspection` — the inspector already
  // shows every field the server sent, and a second path for one of them would be a second thing to keep in step.
  const interior = Number(inspection?.fields.find((f) => f.name === 'interior')?.value ?? 0);
  const door = canViewRealm ? doorRealmOf(realm, interior) : null;

  // A player can walk in instead: the body is steered to the door's published path and along it, like a bot would be.
  const walkable = playing && doorRealmOf(realm, interior) !== null;

  // The session is anchored on THIS entity. One comparison, because `ride.ts` has already decided which of the ride's
  // two ids is the live one — the asked id before its frame lands, the server's after. Comparing against both here
  // would keep the asked one live for the whole ride, and netIds are reused: an unrelated entity that inherited it in
  // the new view would offer Stop for a ride it has nothing to do with.
  const riding = ridingNetId !== 0 && selectedNetId === ridingNetId;

  return (
    <div className="panel inspector">
      <div className="hud-title">
        {inspection === null ? `#${selectedNetId}` : `${inspection.archetype} #${selectedNetId}`}
      </div>
      {inspection !== null && (
        <table>
          <tbody>
            {inspection.hasPosition && (
              <>
                <tr>
                  <td>Position</td>
                  <td>
                    {inspection.x.toFixed(1)}, {inspection.z.toFixed(1)}
                  </td>
                </tr>
                <tr>
                  <td>Speed</td>
                  <td>
                    {inspection.speedMps.toFixed(2)} m/s
                    {inspection.speedMps > 0 ? ` · ${inspection.headingDeg.toFixed(0)}°` : ''}
                  </td>
                </tr>
                <tr>
                  <td>Motion epoch</td>
                  <td>{inspection.epoch}</td>
                </tr>
              </>
            )}
            {inspection.fields.map((f) => {
              // A level reads in its con colour against the viewer's CL (content-levels.md § 6): neutral until the CL is replicated.
              const con = f.name === 'level' ? conColour(Number(f.value), VIEWER_CL) : null;
              return (
                <tr key={f.name}>
                  <td>{f.name}</td>
                  <td className={con === null ? undefined : `con-${con}`}>{f.value}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <div className="row">
        {door === null ? (
          <button
            className={follow ? 'active' : ''}
            onClick={() => {
              setFollow(!follow);
            }}
          >
            Follow
          </button>
        ) : (
          /* A building that can be walked into gets Enter in place of Follow: following a structure works and means
             nothing, because it never moves. Only an enterable one — most buildings have no door, and an Enter that
             refused would be worse than no button. */
          <button
            title="Put this session inside the building. The server may refuse it."
            onClick={() => {
              viewRealm(door);
            }}
          >
            Enter
          </button>
        )}
        {/* Riding is the SESSION following the subject, which is a different thing from the camera doing it: the
            server re-centres replication on the entity, so it cannot walk out of view and a door takes the viewer with
            it. Hidden against a catalog that does not declare the command, as the realm control is. */}
        {canSpectate && door === null && (
          <button
            className={riding ? 'active' : ''}
            title={
              riding
                ? 'Stop riding: the session goes back to its own camera, in the realm you are in.'
                : 'Make the session follow this entity, through doors and shuttles. The server may refuse it.'
            }
            onClick={() => {
              spectate(riding ? 0 : selectedNetId);
            }}
          >
            {riding ? 'Stop' : 'Ride'}
          </button>
        )}
        {walkable && (
          <button
            title="Walk your character to this building's door and through it. Any movement key stops the walk."
            onClick={() => {
              walkDoor(selectedNetId);
            }}
          >
            Walk in
          </button>
        )}
        <button
          onClick={() => {
            select(0);
          }}
        >
          Close
        </button>
      </div>
    </div>
  );
}
