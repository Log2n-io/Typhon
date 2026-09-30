import type { RealmFrame } from '@typhondb/client';

/**
 * What kind of place a realm is, and therefore which scene the renderer builds for it.
 *
 * Read from {@link RealmView.appTag}, not from the realm kind's NAME. The name is an application string
 * (`TatooineReplication.InteriorKind`) that a rename would silently change under the client, and the tag is a
 * number both sides agree on in one documented layout. The kind name stays available for display.
 */
export type SceneKind = 'planet' | 'interior' | 'space' | 'dungeon';

/** The scene kinds the `appTag`'s top nibble names, in its own order. */
const SCENES: readonly SceneKind[] = ['planet', 'interior', 'space', 'dungeon'];

/**
 * What the demo's `AppTag` says about a realm.
 *
 * The layout is fixed on both sides — `demo/SwgTatooine/Sim/TatooineSim.cs` writes it, this reads it:
 *
 * ```text
 * 31..28  scene      0 planet | 1 interior | 2 space | 3 dungeon
 * 27..20  palette    0 sand | 1 green | 2 brown | ...
 * 19..12  placeSet   which static place/name table (0 = Tatooine's)
 * 11..0   slot       the planet's index, or the interior's index within its planet
 * ```
 *
 * <b>It exists so the client never reproduces the server's realm-id arithmetic.</b> An interior is realm
 * `Planets + planet * InteriorsPerPlanet + portal` (`SimBridge.Portals.cs`), which depends on two CLI flags the
 * client cannot see. Every question the renderer asks — which scene, which palette, which place table — is
 * answered by this one `u32`, which the engine carries opaquely and the `REALM` block already delivers.
 */
export interface AppTagParts {
  readonly scene: SceneKind;
  /** Which ground palette the planet scene tints with; meaningless for the others. */
  readonly palette: number;
  /** Which static place/name table the scene draws; meaningless for space. */
  readonly placeSet: number;
  /** The planet's index, or the interior's index within its planet. */
  readonly slot: number;
}

/**
 * Everything the renderer needs to know about the realm the session is in, derived once per switch.
 *
 * <b>Bounds are carried as a centre and a half-extent, not as a half-extent alone.</b> Every "±8192" in this
 * client assumes a world centred on the origin, and an interior is not one: its grid is `(0,0)...(64,64)`
 * (`WorldBuilder.InteriorEdgeM`). Centre-plus-half makes that a difference in data rather than a special case in
 * every consumer, which is the only reason one camera clamp can serve every scene.
 */
export interface RealmView {
  readonly scene: SceneKind;
  readonly realmId: number;
  readonly generation: number;
  /**
   * The switch identity: `realmId:generation`.
   *
   * <b>Not the realm id alone.</b> Ids are reused once a realm is unregistered, and `(id, generation)` is what
   * the design calls a realm's identity on the wire (12-realms § 1.1) precisely so a client never mistakes a
   * reused id for the realm it replaced.
   */
  readonly key: string;
  readonly appTag: number;
  readonly palette: number;
  readonly placeSet: number;
  readonly slot: number;
  /** Three-dimensional: positions carry an altitude axis and a region is a polyhedron rather than a polygon. */
  readonly deep: boolean;
  /** The realm's replication cell side, metres — the natural grid spacing for a scene that draws one. */
  readonly cellM: number;
  readonly minX: number;
  readonly maxX: number;
  /** Altitude bounds; both 0 in a flat realm, which has no altitude axis at all. */
  readonly minY: number;
  readonly maxY: number;
  readonly minZ: number;
  readonly maxZ: number;
  readonly centreX: number;
  readonly centreZ: number;
  readonly halfX: number;
  readonly halfZ: number;
}

/**
 * How a crossing should read on screen, which is not the question of what the engine did.
 *
 * Every realm change is one `Transaction.Teleport` underneath (`SimBridge.Portals.cs`), so the mechanism says
 * nothing about the presentation. Two things separate them — who started it, and whether the two places have any
 * spatial relationship at all:
 *
 * - `travel` — the viewer asked (a `ViewRealm`), so the client knows it is coming and can fade out FIRST.
 * - `transition` — the world did it (a door), so the client learns at `onRealmChanged`, by which point the store
 *   is already empty. There is nothing left to fade out, and faking it would mean delaying the frame's apply.
 * - `channel` — a jump between places with no relationship, like changing channel. Nothing travels into space in
 *   this demo; the camera simply looks somewhere else.
 */
export type CrossingKind = 'arrive' | 'leave' | 'travel' | 'transition' | 'channel';

/** A flat realm's second ground axis: a `pos2` uses axes 0 and 1 (12-realms § 5.3). */
const GROUND_AXIS_Z = 1;

/** The axis altitude lives on, in a deep realm. */
const DEEP_AXIS_Y = 2;

/** Pulls the four fields out of a demo `AppTag`. Tag 0 is a planet in slot 0 with the default palette and places. */
export function decodeAppTag(tag: number): AppTagParts {
  const bits = tag >>> 0;
  return {
    // A scene nibble this build does not know falls back to the planet rather than throwing: an unknown realm
    // kind from a newer server should cost the scene, not the session.
    scene: SCENES[(bits >>> 28) & 0xf] ?? 'planet',
    palette: (bits >>> 20) & 0xff,
    placeSet: (bits >>> 12) & 0xff,
    slot: bits & 0xfff,
  };
}

/**
 * The renderer's view of a realm frame, or `null` when the session is in no realm.
 *
 * @param frame The `REALM` block's frame, as the SDK decoded it.
 */
export function realmViewOf(frame: RealmFrame | null): RealmView | null {
  if (frame === null) {
    return null;
  }

  const parts = decodeAppTag(frame.appTag);

  // Read the frame's own axes rather than assuming (x, z): that is what lets one object serve a 16 km cube, a
  // 16 km planet and a 64 m room. A flat realm has no altitude axis, so its Y bounds are 0 rather than min[2] —
  // axis 2 of a flat realm is the degenerate slab the grid config leaves there, and it is not a height.
  const minX = frame.min[0];
  const maxX = frame.max[0];
  const minZ = frame.min[GROUND_AXIS_Z];
  const maxZ = frame.max[GROUND_AXIS_Z];

  return {
    scene: parts.scene,
    realmId: frame.realmId,
    generation: frame.generation,
    key: `${frame.realmId}:${frame.generation}`,
    appTag: frame.appTag,
    palette: parts.palette,
    placeSet: parts.placeSet,
    slot: parts.slot,
    deep: frame.deep,
    cellM: frame.cellM,
    minX,
    maxX,
    minY: frame.deep ? frame.min[DEEP_AXIS_Y] : 0,
    maxY: frame.deep ? frame.max[DEEP_AXIS_Y] : 0,
    minZ,
    maxZ,
    centreX: (minX + maxX) * 0.5,
    centreZ: (minZ + maxZ) * 0.5,
    halfX: (maxX - minX) * 0.5,
    halfZ: (maxZ - minZ) * 0.5,
  };
}

/**
 * How the crossing from `previous` to `next` should be presented.
 *
 * @param clientAsked The one thing no tag can carry: the same planet-to-planet move is a shuttle ride the world
 * imposed or a camera move the viewer asked for, and only the caller knows which.
 */
export function crossingKindOf(previous: RealmView | null, next: RealmView | null, clientAsked: boolean): CrossingKind {
  if (next === null) {
    return 'leave';
  }

  if (previous === null) {
    return 'arrive';
  }

  if (previous.scene === 'space' || next.scene === 'space') {
    return 'channel';
  }

  return clientAsked ? 'travel' : 'transition';
}

/**
 * A name for a realm, from the realm alone.
 *
 * <b>Deliberately derived rather than looked up.</b> The server knows the real names and will publish them
 * (`/typhon/demo.json`), but a client that can only name the realms a directory told it about goes blank exactly
 * when something unexpected happens — a dungeon that opened this minute, a server too old to serve the list. This
 * always says something true, and a fetched name replaces it when there is one.
 */
export function realmLabel(view: RealmView | null): string {
  return view === null ? 'No realm' : labelForAppTag(view.appTag);
}

/**
 * The same name, for a realm known only by its `AppTag` — the realm inventory's rows, which arrive over HTTP and never
 * as a `REALM` frame.
 *
 * Shared with {@link realmLabel} rather than repeated, so a room cannot be called one thing in the HUD and another in
 * the realm panel two centimetres away.
 */
export function labelForAppTag(tag: number): string {
  const { scene, slot } = decodeAppTag(tag);
  switch (scene) {
    case 'space':
      return 'Space';
    case 'interior':
      return `Interior ${slot}`;
    case 'dungeon':
      return `Dungeon ${slot}`;
    default:
      return `Planet ${slot}`;
  }
}

/**
 * How far back the map camera should sit on arriving in a realm, metres.
 *
 * Scaled from the realm's own extent rather than tabulated per scene: the whole point of carrying a half-extent is
 * that a 16 km planet and a 64 m room are the same problem at two scales. The floor keeps a very small realm from
 * putting the eye inside the geometry, and `MapCamera` clamps the result to its own range anyway.
 *
 * This belongs to the camera more than to the realm and will move onto the scene profile when there is one; it is
 * here so the switch that needs it can be written and tested before any scene work exists.
 */
export function defaultDistanceFor(view: RealmView): number {
  return Math.max(30, Math.max(view.halfX, view.halfZ) * 1.6);
}

/**
 * The realm a building's door leads to, or `null` when there is none to go to.
 *
 * <b>The client is told the rule, not the arithmetic.</b> `portal` is the building's slot in its planet's door list —
 * which a client cannot derive, because enterability comes from a building's index within its city and that is on no
 * wire — and `first` / `perPlanet` are the layout the server publishes at `/typhon/demo.json`. Putting the two together
 * here keeps the one multiplication in a pure function with a test, rather than inline in a click handler.
 *
 * @param from The realm the viewer is in. Only a planet has doors; its `slot` is which planet.
 * @param portal The building's door slot, or -1 for a building that cannot be entered.
 */
export function interiorRealmOf(
  from: RealmView | null,
  portal: number,
  layout: { readonly first: number; readonly perPlanet: number } | null,
): number | null {
  if (from === null || layout === null || from.scene !== 'planet') {
    return null;
  }

  if (!Number.isInteger(portal) || portal < 0 || portal >= layout.perPlanet) {
    return null;
  }

  return layout.first + from.slot * layout.perPlanet + portal;
}

/** Whether two views are the same realm incarnation: the test the per-frame switch check makes. */
export function sameRealm(a: RealmView | null, b: RealmView | null): boolean {
  return a === null || b === null ? a === b : a.key === b.key;
}
