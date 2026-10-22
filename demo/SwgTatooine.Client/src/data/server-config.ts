import type { PlanBox } from '../app/structure-collision';

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
/**
 * Where this server's published assets are, and <b>which</b> catalogue version it is pinned to.
 *
 * <b>Announced rather than discovered, for the same reason every speed is.</b> The server reads the same descriptors the
 * client draws — `collision`, `portals`, `spawns` — so a run where the two read different catalogue versions is a run
 * where a wall is in two places. `catalogue.json` is a mutable pointer and two readers of a mutable pointer agree only by
 * luck, so what arrives here is the immutable `catalogue.<version>.json` the server resolved at startup.
 */
export interface AssetSet {
  /** The folder URL every asset is fetched from, with its trailing slash. One root, never sharded. */
  readonly base: string;
  /** The immutable catalogue's file name. */
  readonly catalogue: string;
  /**
   * The asset a structure is, by the `asset` index it replicates.
   *
   * <b>The server's mapping, not ours.</b> It picks which asset stands where — a city's terminals are drawn between
   * three — and it is the side that pushes bodies out of that asset's walls. A client deriving its own would
   * eventually name a different building than the one it collides with, and the two could not even agree by accident:
   * the client numbers entities by netId and the world builds them by EntityId.
   */
  readonly structures: readonly string[];
  /**
   * The blocking geometry of each asset, by the same index: `[minX, minZ, maxX, maxZ]` per primitive, in the asset's
   * own frame.
   *
   * <b>The server's own boxes, not a re-reading of the descriptors.</b> Deriving them here meant also knowing which
   * assets the server's blocking rule had been verified against — a list that was on no wire — so the client invented
   * a wall across the shuttleport's landing pad and fought the authority about it. An empty array means "this asset
   * collides as its footprint", which is what the server does.
   */
  readonly collision: readonly (readonly PlanBox[])[];
}

export interface ServerConfig {
  /** The god camera's largest region edge in metres, as `--god-region` gave it; 0 for a whole-world camera. */
  readonly godRegionMaxEdgeM: number;
  /**
   * The largest view radius the server will actually honour, metres — half {@link godRegionMaxEdgeM}, because the client
   * sends the smallest square containing its disc. 0 when the camera has no client-driven region at all.
   */
  readonly maxViewRadiusM: number;
  /**
   * The range a PLAYER's own interest sphere may be set over, metres, or `null` from a server that does not publish it.
   *
   * <b>A different mechanism from {@link maxViewRadiusM} above, which is a god camera's region.</b> A player session is a
   * sphere the server widens in place (`SetViewRadius` → the engine's `SetRadius`), between its profile's own radius and
   * the ceiling the replication grid's window bound allows. Both ends come from the server for the reason every speed
   * does: a radius mirrored here would be a figure the server can never correct.
   */
  readonly player: {
    readonly radiusM: number;
    readonly maxRadiusM: number;
    /** What the server actually serves before the slider is touched; `radiusM` from a server that does not say. */
    readonly defaultRadiusM: number;
  } | null;
  /**
   * How wide a body is, and how far the world reaches — what prediction needs to push out of a wall the way the server
   * does, and to stop at the same edge. 0 for a server that publishes neither, which predicts no collision rather than
   * inventing a figure.
   *
   * <b>Read apart from {@link ServerConfig.player}</b>, which is a view radius range: a server that published a body
   * width but not both radii used to yield no collision at all, for two facts that have nothing to do with each other.
   */
  readonly body: { readonly halfM: number; readonly worldHalfM: number };
  /** The realms a god camera may ask for, or `null` from a server that does not publish them. */
  readonly realms: RealmDirectory | null;
  /** The published asset set, or `null` from a server started without one — which is a working run, not a broken one. */
  readonly assets: AssetSet | null;
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

/**
 * The asset set, or `null` for a server that publishes none or publishes something unusable.
 *
 * <b>A relative base only.</b> Everything about a published asset is cached by content-addressed name and fetched
 * same-origin, through this server or through Vite's proxy to it in development; honouring an absolute URL from this
 * document would let the one answer a page trusts about where its own code's data lives point anywhere at all. The day
 * assets move to a CDN, that is a decision taken here, in code, and not one a server can take on the client's behalf.
 */
function readAssets(value: unknown): AssetSet | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const { base, catalogue, structures, collision } = value as Record<string, unknown>;
  if (typeof base !== 'string' || typeof catalogue !== 'string') {
    return null;
  }

  // A server too old to publish the table leaves structures as the renderer's own shapes rather than guessing: an
  // empty list is read everywhere as "this index names nothing".
  const table =
    Array.isArray(structures) && structures.every((id): id is string => typeof id === 'string') ? structures : [];

  // A path on this origin, with a trailing slash so a name appends cleanly; and a bare file name, because a catalogue
  // lives beside the assets it lists and anything with a slash in it is not that.
  if (!base.startsWith('/') || base.startsWith('//') || !base.endsWith('/') || base.includes('..')) {
    return null;
  }

  if (!/^catalogue\.[0-9a-zA-Z]{1,64}\.json$/.test(catalogue)) {
    return null;
  }

  return { base, catalogue, structures: table, collision: readCollision(collision, table.length) };
}

/**
 * The published collision geometry, or empty arrays.
 *
 * Every box is checked: this is parsed input that decides where a body may stand, and a non-finite edge would make a
 * body "inside" that box from everywhere, with nothing to push it back out.
 */
function readCollision(value: unknown, assets: number): readonly (readonly PlanBox[])[] {
  const out: PlanBox[][] = [];
  for (let i = 0; i < assets; i++) {
    out.push([]);
  }

  if (!Array.isArray(value)) {
    return out;
  }

  for (let i = 0; i < Math.min(value.length, assets); i++) {
    const boxes: unknown = value[i];
    if (!Array.isArray(boxes)) {
      continue;
    }

    for (const box of boxes) {
      if (
        !Array.isArray(box) ||
        box.length < 4 ||
        !box.every((n): n is number => typeof n === 'number' && Number.isFinite(n))
      ) {
        continue;
      }

      // Six since the character controller landed; a four-number box from an older server is a wall of unknown
      // height, which is the reading that keeps it blocking rather than silently turning into a floor.
      out[i].push({
        minX: box[0],
        minZ: box[1],
        maxX: box[2],
        maxZ: box[3],
        minY: box.length > 5 ? box[4] : 0,
        maxY: box.length > 5 ? box[5] : Number.POSITIVE_INFINITY,
        slopeX: box.length > 7 ? box[6] : 0,
        slopeZ: box.length > 7 ? box[7] : 0,
      });
    }
  }

  return out;
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

    const { godRegionMaxEdgeM, maxViewRadiusM, playerRadiusM, playerMaxRadiusM, playerBodyHalfM } = body as Record<
      string,
      unknown
    >;
    const { playerDefaultRadiusM } = body as Record<string, unknown>;
    if (!isFinitePositive(godRegionMaxEdgeM) || !isFinitePositive(maxViewRadiusM)) {
      return null;
    }

    // Both ends or neither: a range with one end is a slider with one end, and the UI would have to invent the other —
    // which is the figure of its own this exists to avoid. An older server publishes neither and gets `null`, which the
    // panel renders as "this server does not say".
    const player =
      isFinitePositive(playerRadiusM) && isFinitePositive(playerMaxRadiusM) && playerMaxRadiusM >= playerRadiusM
        ? {
            radiusM: playerRadiusM,
            maxRadiusM: playerMaxRadiusM,
            // Clamped into the range, so a server whose three figures disagree cannot put the slider outside its own ends.
            defaultRadiusM: isFinitePositive(playerDefaultRadiusM)
              ? Math.min(Math.max(playerDefaultRadiusM, playerRadiusM), playerMaxRadiusM)
              : playerRadiusM,
          }
        : null;
    const { worldHalfM } = body as Record<string, unknown>;

    return {
      godRegionMaxEdgeM,
      maxViewRadiusM,
      player,
      body: {
        halfM: isFinitePositive(playerBodyHalfM) ? playerBodyHalfM : 0,
        worldHalfM: isFinitePositive(worldHalfM) ? worldHalfM : 0,
      },
      realms: readRealms((body as Record<string, unknown>).realms),
      assets: readAssets((body as Record<string, unknown>).assets),
    };
  } catch {
    // A page served from somewhere else, or an older server. Not knowing is a state the UI handles.
    return null;
  }
}
