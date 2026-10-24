import { evaluateSlot, MAX_MOTION_STRIDE, type ArchetypeStore, type FieldArray } from '@typhondb/client';
import { SELECTED_SPRITE_SCALE, type LayerStyle } from './styles';
import { altitudeField, altitudeOf, Placement } from '../data/placement';
import { structureYaw } from '../assets/structure-assets';
import { AiMode } from '../data/swg-schema';
import { Band, chooseBand, packState, sphereInFrustum } from './view-math';
import type { GroundSampler } from '../terrain/ground-sampler';

/** What every layer needs to know about the frame being drawn. */
export interface FrameView {
  readonly renderTick: number;
  readonly renderFrac: number;
  readonly originX: number;
  readonly originZ: number;
  /** Camera position in render space. */
  readonly eyeX: number;
  readonly eyeY: number;
  readonly eyeZ: number;
  /** Frustum planes in render space (see `view-math.ts`). */
  readonly planes: Float64Array;
  /** CSS pixels per metre at one metre of distance: viewport height / (2 tan(fov / 2)). */
  readonly pixelsPerMetre: number;
  /** Viewport size in CSS pixels: LOD thresholds, sprite and line sizes and picking all count CSS pixels. */
  readonly viewportWidth: number;
  readonly viewportHeight: number;
  readonly selectedNetId: number;
  /** The planet's ground, for standing entities on it. Flat until the bake lands (`terrain/heightfield.ts`). */
  readonly ground: GroundSampler;
  /**
   * One entity whose position comes from somewhere other than the store: the locally predicted avatar (A14), or `null`.
   *
   * <b>Overriding here and not only at the camera is the difference between a predicted camera and a predicted BODY.</b> The
   * mesh of every entity is placed from this loop, so a prediction that only reached the camera would slide the view off a
   * character still standing where the server last confirmed it — which looks worse than the latency it removes. The cost is
   * one integer compare per entity per frame, on the one archetype that holds the player.
   */
  readonly predicted: PredictedOverride | null;
  /**
   * The entities this loop must NOT draw: bodies something else is drawing in their place.
   *
   * The rigged avatar is one of them, and every structure standing as a published asset is another. Both drawn would be
   * the renderer's own shape inside the model — and the cheap half would be the visible one at any distance where its
   * sprite is the larger.
   *
   * <b>Skipped before the band is chosen, and that is deliberate</b>: a hidden entity leaves no band state behind, so
   * the frame it stops being hidden it re-enters at whatever band its size then deserves rather than at a stale one.
   */
  readonly hidden: ReadonlySet<number>;
  /**
   * Where a character layer puts its near band instead of packing it as a box.
   *
   * `null` leaves every layer drawing boxes, which is what a session with no published characters does — and what
   * every layer that is not characters does regardless.
   */
  readonly crowd: CrowdBody[] | null;
  /**
   * Whether the crowd is actually drawing the bodies it is handed.
   *
   * Until it is, a character layer collects for the crowd AND still draws its own box, so the world is never emptier
   * than it was before the crowd existed. A body drawn twice is a visible bug; a body drawn by nobody is an invisible
   * one, and the second is worse.
   */
  readonly crowdDraws: boolean;
  /** How many of {@link crowd} this frame filled. The array keeps its capacity, so its length is not the count. */
  crowdCount: number;
  /**
   * Multiplies a replicated velocity into metres per SECOND.
   *
   * <b>It is `1000 / tickPeriodMs`, and it is here because forgetting it is a defect this client has shipped.</b>
   * What arrives on the wire is a velocity per TICK, which is what keeps a changing tick period from distorting
   * history; a clip rate derived from it directly makes a sprinting body play its walk at the slowest rate it has.
   */
  readonly perSecond: number;
}

/**
 * One replicated body, as a character layer hands it to the crowd.
 *
 * <b>Reused across frames, never allocated per body.</b> The array below is rebuilt every frame from the bodies in
 * the near band; writing a fresh literal per body put an object per body per frame on the packer's hot path. A caller
 * reads a record within the frame it was handed and keeps nothing.
 */
export interface CrowdBody {
  netId: number;
  x: number;
  y: number;
  z: number;
  /** Ground speed, metres per second. */
  speed: number;
  heading: number;
  /** On its swoop: drawn seated, and given a swoop under it. */
  mounted: boolean;
  /** A creature's replicated `template`, or -1 for a body that is not one (a player, a city NPC). */
  template: number;
  /** A creature's replicated `skin`: which of its template's bodies it wears. */
  skin: number;
  /** Dead: plays its death once and holds the last frame until the server destroys it. */
  dead: boolean;
  /**
   * The outfit this body wears, as an index into its crowd layer's outfits; -1 lets the layer choose by netId.
   *
   * Set by whoever routes bodies between layers (`app/crowd-router.ts`), not by the packer: which layer draws a body, and
   * in what, is a fact about the published assets, and the packer knows only the store.
   */
  outfit: number;
  /** How many blows this body has struck, as the router counts the server's `Attack` events. A change plays one. */
  strikes: number;
}

/**
 * The crowd's hand-over array, grown in place.
 *
 * @param bodies The array the app reuses.
 * @param n How many are already written this frame.
 * @returns The record to write into.
 */
export function crowdSlot(bodies: CrowdBody[], n: number): CrowdBody {
  if (n >= bodies.length) {
    bodies.push({
      netId: 0,
      x: 0,
      y: 0,
      z: 0,
      speed: 0,
      heading: 0,
      mounted: false,
      template: -1,
      skin: 0,
      dead: false,
      outfit: -1,
      strikes: 0,
    });
  }

  return bodies[n];
}

/** No entity hidden, for a caller that hides none. */
export const NONE_HIDDEN: ReadonlySet<number> = new Set<number>();

/** A position substituted for one entity's own during packing. */
export interface PredictedOverride {
  readonly netId: number;
  readonly x: number;
  readonly z: number;
}

interface FieldReaders {
  readonly style: FieldArray | null;
  /** The style field is a flag (a lair's mission id): any non-zero value selects style 1. */
  readonly styleIsFlag: boolean;
  readonly mode: FieldArray | null;
  /** The server's own altitude for each entity, or `null` when the catalog has none (the mock). */
  readonly ground: FieldArray | null;
  /**
   * Which way a structure faces, in quarter turns, or `null` for an archetype that does not replicate one.
   *
   * <b>The world's orientation beats the renderer's guess.</b> `gridYaw` invents a quarter turn from the position, which
   * is fine for a box and wrong the moment the thing has a door and a collision shape the server pushes bodies out of.
   * Null still means `gridYaw`, which is what a lair, a prop and the mock still get.
   */
  readonly yaw: FieldArray | null;
  /** Whether a player rides its swoop (1), for the crowd's seated pose; `null` for an archetype that never rides. */
  readonly mounted: FieldArray | null;
  /** A creature's outfit among its template's bodies; `null` for every other archetype. */
  readonly skin: FieldArray | null;
  /** Whether {@link style} is a creature template, which the crowd draws the body by. */
  readonly creature: boolean;
}

const NO_FIELDS: FieldReaders = {
  style: null,
  styleIsFlag: false,
  mode: null,
  ground: null,
  yaw: null,
  mounted: null,
  skin: null,
  creature: false,
};
const INITIAL_CAPACITY = 1024;

/**
 * The SWG fields that pick an archetype's style and mode, chosen by the archetype's NAME: the catalog numbers archetypes
 * in its own order, so an index says nothing (`data/archetypes.ts`).
 *
 * Every field is asked for by name and may be absent — a catalog that does not replicate it yields `null` here and the
 * archetype draws in its base style rather than reading a neighbouring field's bytes. The live catalog does omit some:
 * a `Creature` has no `template`, a `CreatureLair` no `missionId`.
 */
function readersFor(name: string, store: ArchetypeStore): FieldReaders {
  switch (name) {
    case 'WorldObject':
      return {
        style: optional(store, 'kind'),
        styleIsFlag: false,
        mode: null,
        ground: altitudeField(store),
        yaw: optional(store, 'yaw'),
        mounted: null,
        skin: null,
        creature: false,
      };
    case 'CreatureLair':
      return {
        style: optional(store, 'missionId'),
        styleIsFlag: true,
        mode: null,
        ground: altitudeField(store),
        yaw: null,
        mounted: null,
        skin: null,
        creature: false,
      };
    case 'Creature':
      return {
        style: optional(store, 'template'),
        styleIsFlag: false,
        mode: optional(store, 'mode'),
        ground: altitudeField(store),
        yaw: null,
        mounted: null,
        skin: optional(store, 'skin'),
        creature: true,
      };
    case 'CityNpc':
      return {
        style: null,
        styleIsFlag: false,
        mode: optional(store, 'mode'),
        ground: altitudeField(store),
        yaw: null,
        mounted: null,
        skin: null,
        creature: false,
      };
    case 'Player':
      return {
        style: null,
        styleIsFlag: false,
        mode: optional(store, 'activity'),
        ground: altitudeField(store),
        yaw: null,
        mounted: optional(store, 'mounted'),
        skin: null,
        creature: false,
      };
    default:
      return NO_FIELDS;
  }
}

/**
 * A field the store may not have. `ArchetypeStore.field` THROWS on a name the schema does not declare, which is right for
 * a store that is asked for a field it must have and wrong here: which fields exist is the catalog's to decide, and it
 * decides differently from the mock — a live `Creature` has no `template`, a live `CreatureLair` no `missionId`. Asking
 * with `field` put an exception in the render loop on the first frame of the first live session.
 */
function optional(store: ArchetypeStore, name: string): FieldArray | null {
  const index = store.fieldIndex(name);
  return index < 0 ? null : store.fieldAt(index);
}

/**
 * The CPU half of a layer, apart from Babylon so it can be tested: every live entity of one store evaluated at render time,
 * culled, banded and packed into the near and far instance arrays (`x, z, yaw, packed` at stride 4) with the netId of each
 * instance beside it.
 */
export class LayerPacker {
  readonly archetype: number;
  readonly style: LayerStyle;
  /** The only style index this layer draws, or -1 for all of them. See the constructor. */
  readonly onlyStyle: number;
  /** Whether this layer's near band is drawn by the crowd as rigged characters rather than as boxes. */
  drawsCharacters = false;
  /** A style index this layer skips, or -1. */
  readonly omitStyle: number;

  /** Per near instance: `(x, y, z, packed)` in render space, altitude included (CLI3D-04). */
  nearData = new Float32Array(0);
  /** Per far instance: the same four, though a sprite ignores everything but the point and the state word. */
  farData = new Float32Array(0);
  /** Per near instance: its yaw, in its own buffer because `nearData`'s four slots are spoken for. */
  nearYaw = new Float32Array(0);
  /** Entity packed at each instance, captured at pack time for picking. */
  nearNetIds = new Uint32Array(0);
  farNetIds = new Uint32Array(0);
  nearCount = 0;
  farCount = 0;
  /**
   * The near bodies the crowd drew instead of this layer, in the near band's layout: drawn by nobody HERE, and still
   * picked here.
   *
   * <b>A body the crowd drew could not be clicked.</b> Diverting it skipped the arrays the pick reads, so a city NPC or a
   * player near enough to be a character was unselectable, and the creatures — which a click targets — would have been
   * too. Kept apart from {@link nearData} so the box mesh's instance count stays the boxes it draws.
   */
  heldData = new Float32Array(0);
  heldYaw = new Float32Array(0);
  heldNetIds = new Uint32Array(0);
  heldCount = 0;
  /** Instances each output array holds; bumped with {@link arraysVersion} when the arrays are replaced. */
  capacity = 0;
  arraysVersion = 0;

  private store: ArchetypeStore | null = null;
  private storeVersion = -1;
  private fields: FieldReaders = NO_FIELDS;
  private readonly motion = new Float64Array(MAX_MOTION_STRIDE);
  /** Rewritten per entity, never allocated: the one place that knows where altitude and velocity live. */
  private readonly placement = new Placement();
  /** Per slot: the entity whose state the slot arrays describe, its LOD band and its last heading. */
  private seenNetId = new Uint32Array(0);
  private band = new Uint8Array(0);
  private yaw = new Float32Array(0);

  /**
   * @param archetype Which archetype's store this layer draws.
   * @param style Its art.
   * @param onlyStyle Draw only entities whose style index is this one, or -1 for all.
   * @param omitStyle Skip entities whose style index is this one, or -1 for none.
   *
   * @remarks
   * <b>The two filters are what let one archetype be drawn by two layers with different SHAPES.</b> A shape is a mesh
   * and a mesh belongs to a layer, so per-instance size and colour were the only things that could vary within one —
   * which is why every structure was a box. A shuttleport is now a second layer over the same store that takes only
   * `kind` 2 while the first omits it: two meshes, one archetype, and no instance drawn twice.
   */
  constructor(archetype: number, style: LayerStyle, onlyStyle = -1, omitStyle = -1) {
    this.archetype = archetype;
    this.style = style;
    this.onlyStyle = onlyStyle;
    this.omitStyle = omitStyle;
    this.ensureCapacity(INITIAL_CAPACITY);
  }

  get bound(): ArchetypeStore | null {
    return this.store;
  }

  /** Binds to a store: per-slot state starts over. */
  bind(store: ArchetypeStore | null): void {
    this.store = store;
    this.storeVersion = -1;
    this.fields = NO_FIELDS;
    this.seenNetId.fill(0);
    this.nearCount = 0;
    this.farCount = 0;
  }

  /** Evaluates, culls, bands and packs every live entity of the bound store. */
  pack(view: FrameView): void {
    const store = this.store;
    if (store === null) {
      this.nearCount = 0;
      this.farCount = 0;
      return;
    }

    if (store.version !== this.storeVersion) {
      this.resizeSlotState(store);
      // From the STORE's own name, not from this layer's index: the catalog numbers archetypes in its order, not ours.
      this.fields = readersFor(store.schema.name, store);
    }

    const count = store.liveCount;
    this.ensureCapacity(count);

    const fields = this.fields;
    const style = this.style;
    const bounds = style.bounds;
    const spriteLift = style.spriteLift;
    const byVelocity = style.heading === 'velocity';
    // A sprite's world radius at distance d: its CSS pixel radius (the selected size, the largest) × d / pixels-per-metre.
    const spriteMetresPerMetre = (style.spritePixels * SELECTED_SPRITE_SCALE * 0.5) / view.pixelsPerMetre;
    const motion = this.motion;
    const live = store.live;
    const netIds = store.netIds;
    const seenNetId = this.seenNetId;
    const bands = this.band;
    const yaws = this.yaw;
    const nearData = this.nearData;
    const farData = this.farData;
    const nearYaw = this.nearYaw;
    const nearNetIds = this.nearNetIds;
    const farNetIds = this.farNetIds;
    const tick = view.renderTick;
    const frac = view.renderFrac;
    let near = 0;
    let far = 0;
    let held = 0;

    const dims = store.dims;
    const onlyStyle = this.onlyStyle;
    const omitStyle = this.omitStyle;
    const at = this.placement;
    // Resolved once per frame, not per entity: the override names a netId and the loop has the slot.
    const predicted = view.predicted;
    const predictedNetId = predicted === null ? 0 : predicted.netId;
    const hidden = view.hidden;
    // Null for every layer that is not characters, so nothing else pays for the branch below.
    const crowd = this.drawsCharacters ? view.crowd : null;
    for (let i = 0; i < count; i++) {
      const slot = live[i];
      // <b>Before the evaluation, not after it.</b> A hidden entity is one something else draws — the rigged avatar,
      // a structure standing as its published asset — and evaluating its motion, its altitude and its frustum sphere
      // only to drop it is work for nothing. The set is empty for every layer but the two that have one, so the size
      // check is what keeps the crowd from paying a hash lookup at all.
      if (hidden.size !== 0 && hidden.has(netIds[slot])) {
        continue;
      }

      evaluateSlot(store, slot, tick, frac, motion, 0);
      at.read(dims, motion, 0);
      // A14: the predicted body. The VELOCITY is left authoritative on purpose — it is what the mesh takes its heading from,
      // and the server's is the one that turns the character to face where it is actually going.
      const own = predictedNetId !== 0 && netIds[slot] === predictedNetId;
      const rx = (own ? predicted!.x : at.x) - view.originX;
      const rz = (own ? predicted!.z : at.z) - view.originZ;
      // Altitude is not offset: the render origin only ever slides along the ground (`05-client.md` § 6). It comes from
      // the SERVER when the catalog carries it (terrain rung (b)) and from this client's own field otherwise — see
      // `altitudeOf`, which is the only place that decision is made.
      // Sampled at the PREDICTED ground position when the client owns the heightfield, or the predicted body walks a dune at
      // the height of where the server last confirmed it — a few centimetres, but it reads as the feet sinking. When the
      // server supplies altitude per entity there is nothing to re-sample and its figure stands.
      const ry =
        own && fields.ground === null
          ? at.y + view.ground.heightAt(predicted!.x, predicted!.z)
          : altitudeOf(fields.ground, slot, at, view.ground);

      const raw = fields.style === null ? 0 : fields.style[slot];
      const styleIndex = fields.styleIsFlag ? (raw > 0 ? 1 : 0) : raw;
      // Before the frustum test and before any slot state is touched: a filtered-out entity belongs to the other layer
      // over this store, which keeps its own band and heading for it.
      if ((onlyStyle >= 0 && styleIndex !== onlyStyle) || styleIndex === omitStyle) {
        continue;
      }

      const bound = styleIndex >= 0 && styleIndex < bounds.radius.length ? styleIndex : 0;
      const cy = ry + bounds.centerY[bound];
      const dx = rx - view.eyeX;
      const dy = cy - view.eyeY;
      const dz = rz - view.eyeZ;
      const distance = Math.sqrt(dx * dx + dy * dy + dz * dz) + 1e-3;
      // The sphere holds the mesh, and the sprite too: drawn at its own lift, which may sit off the mesh's centre.
      const spriteRadius = distance * spriteMetresPerMetre + Math.abs(cy - (ry + spriteLift));
      const radius = Math.max(bounds.radius[bound], spriteRadius);
      if (!sphereInFrustum(view.planes, rx, cy, rz, radius)) {
        continue;
      }

      const netId = netIds[slot];
      if (seenNetId[slot] !== netId) {
        seenNetId[slot] = netId;
        bands[slot] = Band.Unset;
        yaws[slot] = byVelocity
          ? (netId * 2.399963) % (Math.PI * 2)
          : fields.yaw === null
            ? gridYaw(at.x, at.z)
            : structureYaw(fields.yaw[slot]);
      }

      const pixels = (bounds.extent[bound] * view.pixelsPerMetre) / distance;
      const band = chooseBand(bands[slot], pixels);
      bands[slot] = band;
      const packed = packState(styleIndex, fields.mode === null ? 0 : fields.mode[slot], netId === view.selectedNetId);

      if (band === Band.Near) {
        // Heading only matters to a mesh, and it comes from the GROUND velocity: a ship climbing keeps its heading.
        // A still entity keeps the last heading it had as one.
        if (byVelocity && (at.vx !== 0 || at.vz !== 0)) {
          yaws[slot] = Math.atan2(at.vx, at.vz);
        }

        // <b>A character layer's near band IS the crowd.</b> Everything a body needs has just been computed — its
        // place in render space, its heading, its ground velocity — so diverting it here costs one branch and avoids
        // evaluating all of it a second time somewhere else. The far band is untouched: below the near threshold a
        // body is a sprite, which is the same seam the crowd's own coarsest tier stops at.
        //
        // The speed handed over is metres per SECOND. What the store carries is per TICK — the defect this client has
        // already shipped once, where a sprinting character played the walk clip at its slowest rate.
        if (crowd !== null) {
          // Into the record already there, not a new one: see `CrowdBody`. `view.crowdCount` is the live length, so
          // the array keeps its capacity across frames and the crowd reads exactly what this frame wrote.
          const body = crowdSlot(crowd, view.crowdCount);
          body.netId = netId;
          body.x = rx;
          body.y = ry;
          body.z = rz;
          body.speed = Math.hypot(at.vx, at.vz) * view.perSecond;
          body.heading = yaws[slot];
          body.mounted = fields.mounted !== null && fields.mounted[slot] === 1;
          body.template = fields.creature ? raw : -1;
          body.skin = fields.skin === null ? 0 : fields.skin[slot];
          body.dead = fields.creature && fields.mode !== null && fields.mode[slot] === AiMode.Dead;
          body.outfit = -1;
          view.crowdCount++;
          // <b>Only STOP drawing the box once the crowd is known to be drawing a body in its place.</b> Diverting
          // unconditionally is how a bring-up bug became a worse regression than the thing it was replacing: the
          // crowd drew nothing, the boxes were already gone, and the street was empty. The flag is owned by the crowd
          // itself and is false until it has put pixels on screen.
          if (view.crowdDraws) {
            const h = held * 4;
            this.heldData[h] = rx;
            this.heldData[h + 1] = ry;
            this.heldData[h + 2] = rz;
            this.heldData[h + 3] = packed;
            this.heldYaw[held] = yaws[slot];
            this.heldNetIds[held++] = netId;
            continue;
          }
        }

        // (x, y, z, packed), with yaw in its own buffer: the four slots were full, and widening the state word to hold a
        // yaw would have pushed it past the 24 bits a float32 carries exactly. Only the mesh reads yaw — a sprite is a
        // disc — so the far band does not pay for it.
        const b = near * 4;
        nearData[b] = rx;
        nearData[b + 1] = ry;
        nearData[b + 2] = rz;
        nearData[b + 3] = packed;
        nearYaw[near] = yaws[slot];
        nearNetIds[near++] = netId;
      } else {
        const b = far * 4;
        farData[b] = rx;
        farData[b + 1] = ry;
        farData[b + 2] = rz;
        farData[b + 3] = packed;
        farNetIds[far++] = netId;
      }
    }

    this.nearCount = near;
    this.farCount = far;
    this.heldCount = held;
  }

  private resizeSlotState(store: ArchetypeStore): void {
    const capacity = store.capacity;
    if (this.seenNetId.length !== capacity) {
      const seen = new Uint32Array(capacity);
      const band = new Uint8Array(capacity);
      const yaw = new Float32Array(capacity);
      const kept = Math.min(capacity, this.seenNetId.length);
      seen.set(this.seenNetId.subarray(0, kept));
      band.set(this.band.subarray(0, kept));
      yaw.set(this.yaw.subarray(0, kept));
      this.seenNetId = seen;
      this.band = band;
      this.yaw = yaw;
    }

    this.storeVersion = store.version;
  }

  private ensureCapacity(count: number): void {
    if (count <= this.capacity) {
      return;
    }

    let capacity = Math.max(INITIAL_CAPACITY, this.capacity);
    while (capacity < count) {
      capacity *= 2;
    }

    this.capacity = capacity;
    this.nearData = new Float32Array(capacity * 4);
    this.farData = new Float32Array(capacity * 4);
    this.nearYaw = new Float32Array(capacity);
    this.nearNetIds = new Uint32Array(capacity);
    this.farNetIds = new Uint32Array(capacity);
    this.heldData = new Float32Array(capacity * 4);
    this.heldYaw = new Float32Array(capacity);
    this.heldNetIds = new Uint32Array(capacity);
    this.arraysVersion++;
  }
}

/** A stable heading for things laid out on streets: a multiple of 90° hashed from the position. */
function gridYaw(x: number, z: number): number {
  const h = Math.sin(x * 12.9898 + z * 78.233) * 43758.5453;
  return Math.floor((h - Math.floor(h)) * 4) * (Math.PI / 2);
}
