/**
 * The realms this server has, as it describes them.
 *
 * <b>Planets and space are listed; interiors are described.</b> The shipping map has around six hundred enterable
 * buildings per planet, so a two-planet run has more than twelve hundred interior realms — a range rather than a list,
 * and one nobody could use as a menu. So the server publishes the rule instead, and a client that computes an id from
 * `first + planet * perPlanet + portal` is following a rule it was TOLD rather than reconstructing one from flags it
 * cannot see.
 *
 * Dungeons are deliberately absent: their realms exist only while a party is inside one, so a directory entry for one
 * would be true when it was fetched and wrong when it was clicked.
 */
export interface RealmDirectory {
  readonly planets: readonly { readonly id: number; readonly appTag: number }[];
  readonly interiors: { readonly first: number; readonly perPlanet: number };
  /** The space realm, when this server was started with one. */
  readonly space: { readonly id: number; readonly appTag: number } | null;
}

/**
 * What the demo server says it was started with (`/typhon/demo.json`).
 *
 * <b>Why the server has to tell us at all.</b> A `ClientRegion` larger than the god profile's ceiling is silently shrunk
 * about its centroid, and the ceiling is on no wire the client can read — so a viewer asking for a 4 km radius was served
 * 1.5 km and told it had 4 (Typhon #1075). The engine should eventually say so itself; until it does, the one process
 * that owns both the server and this page publishes its own configuration and the slider stops offering what cannot be
 * had.
 *
 * Absent — a mock session, a server too old to serve it, a page opened from a file — leaves the limit unknown, and the
 * UI says "asked" rather than claiming a number it cannot stand behind.
 */
export interface ServerConfig {
  /** The god camera's largest region edge in metres, as `--god-region` gave it; 0 for a whole-world camera. */
  readonly godRegionMaxEdgeM: number;
  /**
   * The largest view radius the server will actually honour, metres — half {@link godRegionMaxEdgeM}, because the client
   * sends the smallest square containing its disc. 0 when the camera has no client-driven region at all.
   */
  readonly maxViewRadiusM: number;
  /** The realms a god camera may ask for, or `null` from a server that does not publish them. */
  readonly realms: RealmDirectory | null;
}

function isFinitePositive(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

/** A realm the directory lists, or `null` for anything that is not one. Every field is checked: this is parsed input. */
function readRealm(value: unknown): { id: number; appTag: number } | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const { id, appTag } = value as Record<string, unknown>;
  return isFinitePositive(id) && isFinitePositive(appTag) ? { id, appTag } : null;
}

/**
 * The realm directory, or `null` when the server published none or published something unusable.
 *
 * <b>A partial directory is treated as no directory.</b> A selector built from half a list is worse than one that says
 * it does not know: the missing half is invisible, so the viewer cannot tell a short list from a small world.
 */
function readRealms(value: unknown): RealmDirectory | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const { planets, interiors, space } = value as Record<string, unknown>;
  if (space !== undefined && readRealm(space) === null) {
    // A malformed `space` rejects the whole directory, exactly as a malformed planet does. Reading it as `null` would
    // report a server WITHOUT a space realm rather than one this client cannot read, which is a different and quieter
    // lie than the one the policy above is written to avoid. Absent is still absent — only present-and-broken fails.
    return null;
  }

  if (!Array.isArray(planets) || planets.length === 0) {
    return null;
  }

  const read = planets.map(readRealm);
  if (read.some((planet) => planet === null)) {
    return null;
  }

  const bounds = typeof interiors === 'object' && interiors !== null ? (interiors as Record<string, unknown>) : null;
  if (bounds === null || !isFinitePositive(bounds['first']) || !isFinitePositive(bounds['perPlanet'])) {
    return null;
  }

  return {
    planets: read as { id: number; appTag: number }[],
    interiors: { first: bounds['first'], perPlanet: bounds['perPlanet'] },
    space: readRealm(space),
  };
}

/** Reads the server's configuration, or `null` when there is none to read. Never throws. */
export async function fetchServerConfig(): Promise<ServerConfig | null> {
  try {
    const response = await fetch('typhon/demo.json', { cache: 'no-store' });
    if (!response.ok) {
      return null;
    }

    const body: unknown = await response.json();
    if (typeof body !== 'object' || body === null) {
      return null;
    }

    const { godRegionMaxEdgeM, maxViewRadiusM } = body as Record<string, unknown>;
    if (!isFinitePositive(godRegionMaxEdgeM) || !isFinitePositive(maxViewRadiusM)) {
      return null;
    }

    return { godRegionMaxEdgeM, maxViewRadiusM, realms: readRealms((body as Record<string, unknown>).realms) };
  } catch {
    // A page served from somewhere else, or an older server. Not knowing is a state the UI handles.
    return null;
  }
}
