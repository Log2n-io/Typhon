import { useTerrain } from '../state/terrain-store';

/**
 * What is shown while the planet is still being obtained.
 *
 * <b>It says which of the two things is happening, because they differ by two orders of magnitude.</b> Reading the local
 * database is a few hundred milliseconds and nobody should be told to be patient for it; baking is ~2 s of eight-core work
 * and a bare spinner for that long reads as a hang. Saying "building the planet — this happens once" also makes the next
 * visit's speed legible instead of mysterious.
 *
 * It covers the canvas rather than replacing it: the engine, the socket and the session are all alive underneath, and only
 * the world is waiting. Tearing the canvas down and rebuilding it would cost a WebGL context for no gain.
 */
export function TerrainLoading() {
  const phase = useTerrain((s) => s.phase);

  return (
    <div className="terrain-loading">
      <div className="terrain-loading-panel">
        <div className="terrain-loading-title">{phase === 'baking' ? 'Building the planet' : 'Loading the planet'}</div>
        <div className="terrain-loading-note">
          {phase === 'baking'
            ? 'Sixteen million height samples, once. The next visit reads it back from this browser.'
            : 'Reading the terrain this browser already built.'}
        </div>
        <div className="terrain-loading-bar">
          <div className="terrain-loading-fill" />
        </div>
      </div>
    </div>
  );
}
