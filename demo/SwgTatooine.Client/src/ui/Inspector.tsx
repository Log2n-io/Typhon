import { ARCHETYPE_LABELS } from '../data/swg-schema';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

/** The picked entity, live: what the server replicated for it, and where it is going. */
export function Inspector() {
  const selectedNetId = useUi((s) => s.selectedNetId);
  const follow = useUi((s) => s.follow);
  const select = useUi((s) => s.select);
  const setFollow = useUi((s) => s.setFollow);
  const published = useStats((s) => s.stats?.inspection ?? null);
  if (selectedNetId === 0) {
    return null;
  }

  // Stats arrive four times a second: until then, what was published may describe the previous selection.
  const inspection = published?.netId === selectedNetId ? published : null;

  return (
    <div className="panel inspector">
      <div className="hud-title">
        {inspection === null ? `#${selectedNetId}` : `${ARCHETYPE_LABELS[inspection.archetype]} #${selectedNetId}`}
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
            {inspection.fields.map((f) => (
              <tr key={f.name}>
                <td>{f.name}</td>
                <td>{f.value}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="row">
        <button
          className={follow ? 'active' : ''}
          onClick={() => {
            setFollow(!follow);
          }}
        >
          Follow
        </button>
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
