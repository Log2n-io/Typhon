/** The controller's small decisions, apart from WebGL and the DOM so they can be tested. */

/**
 * Mean and nearest-rank 95th percentile of the first `n` samples, sorted into `scratch` (insertion sort: `n` is a few
 * hundred at most, at 4 Hz). Writes `[mean, p95]` into `out`; both 0 when there is no sample.
 */
export function meanAndP95(samples: Float64Array, n: number, scratch: Float64Array, out: Float64Array): void {
  if (n <= 0) {
    out[0] = 0;
    out[1] = 0;
    return;
  }

  let sum = 0;
  for (let i = 0; i < n; i++) {
    const v = samples[i];
    sum += v;
    let j = i;
    while (j > 0 && scratch[j - 1] > v) {
      scratch[j] = scratch[j - 1];
      j--;
    }

    scratch[j] = v;
  }

  out[0] = sum / n;
  out[1] = scratch[Math.ceil(n * 0.95) - 1];
}

/** Whether the god camera's region is due: moved 5 % of the radius or a new radius, and not sent in the last `intervalMs`. */
export function regionDue(
  x: number,
  z: number,
  radius: number,
  sentX: number,
  sentZ: number,
  sentRadius: number,
  nowMs: number,
  sentMs: number,
  intervalMs: number,
): boolean {
  // NaN (nothing sent yet) fails every comparison: `!(moved <= …)` makes it due.
  const moved = Math.hypot(x - sentX, z - sentZ);
  const changed = !(moved <= radius * 0.05) || radius !== sentRadius;
  return changed && nowMs - sentMs >= intervalMs;
}

/** What {@link overviewPolicy} decided: whether to put a player back behind its avatar, and whether to ask for the god camera. */
export interface OverviewDecision {
  /** The camera is the god camera off a planet: put the player back behind its avatar. */
  readonly backToAvatar: boolean;
  /** Ask the server for the god camera's view (`Overview`); otherwise for the player's own. */
  readonly overview: boolean;
}

/**
 * A player's god camera, decided from the camera mode and the realm the session is in.
 *
 * The god camera is a view of a planet's map, so it is wanted on a planet only. A player whose realm is anything else —
 * an interior, space, a dungeon — is put back behind its avatar; before the first realm arrives (`null`) nothing is
 * asked and nothing is forced. A god session is never asked about: its camera already sees the planet.
 */
export function overviewPolicy(playing: boolean, cameraMode: 'god' | 'eye', scene: string | null): OverviewDecision {
  if (!playing) {
    return { backToAvatar: false, overview: false };
  }

  const onPlanet = scene === 'planet';
  const backToAvatar = cameraMode === 'god' && scene !== null && !onPlanet;
  return { backToAvatar, overview: cameraMode === 'god' && onPlanet };
}
