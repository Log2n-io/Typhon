import type { Quat, Vec3 } from '../math/vec';

/**
 * The published asset contract (`SwgTatooine.Assets/ASSET_FORMAT.md`): descriptors at schema 1, the catalogue at schema 2.
 * Everything here is in the asset's **glTF frame**:
 * metres, +Y up, right-handed, positions relative to the asset origin. Converting to the world frame is
 * `asset-frame.ts`'s job and nobody else's.
 *
 * <b>The server reads the same documents.</b> It takes `bounds` for its spatial index and `collision`, `portals`,
 * `spawns` and `interactions` for gameplay, so these types describe a contract shared with `SwgTatooine`, not a client
 * convenience — which is why the catalogue version is announced rather than discovered (`AssetOrigin` in the server).
 *
 * Ported from the asset testbench (`BlenderGenTest/tools/testbench/src/assets/descriptor.ts`).
 */

export interface CatalogueEntry {
  readonly rev: number;
  readonly descriptor: string;
  readonly glb: string;
}

/** An asset's icon (ASSET_FORMAT.md, catalogue `icons`): one PNG per size in pixels, drawn at that size. */
export interface CatalogueIcon {
  readonly rev: number;
  readonly png: Readonly<Partial<Record<string, string>>>;
}

export interface Catalogue {
  readonly schema: number;
  /** Counts up from 1; the immutable file is `catalogue-<version>.json`. */
  readonly version: number;
  readonly assets: Readonly<Partial<Record<string, CatalogueEntry>>>;
  readonly textures: readonly string[];
  /** Optional: a catalogue without it has no icons. */
  readonly icons?: Readonly<Partial<Record<string, CatalogueIcon>>>;
}

/**
 * The file of an asset's icon at one size, or `null` when the catalogue publishes none — no icon for the asset, or not at
 * that size. Never another size: an icon is rendered at its own size, and a scaled one is a blurred one.
 */
export function iconFile(catalogue: Catalogue, assetId: string, size: number): string | null {
  return catalogue.icons?.[assetId]?.png[String(size)] ?? null;
}

export type AssetKind =
  'building' | 'interior' | 'interactable' | 'prop' | 'vehicle' | 'character' | 'creature' | 'weapon' | 'item';

export type ShapeDesc =
  | { readonly type: 'box'; readonly half: Vec3 }
  | { readonly type: 'sphere'; readonly radius: number }
  | { readonly type: 'cylinder'; readonly radius: number; readonly half_height: number }
  | { readonly type: 'capsule'; readonly radius: number; readonly half_height: number };

export interface ColliderDesc {
  readonly shape: number;
  readonly name: string;
  readonly pos: Vec3;
  readonly rot: Quat;
}

/**
 * An interior's navigation (ASSET_FORMAT.md § 5.9): waypoints, the edges walked straight between them, and a cell grid
 * naming for each floor position the node it walks straight to. glTF frame, like everything here.
 */
export interface NavDesc {
  readonly nodes: readonly Vec3[];
  readonly edges: readonly (readonly [number, number])[];
  readonly cells: {
    readonly origin: readonly [number, number];
    readonly size: number;
    readonly cols: number;
    readonly rows: number;
    /** `node[row * cols + col]`; -1 where no position in the cell is clear. */
    readonly node: readonly number[];
  };
}

export interface PortalDesc {
  readonly id: string;
  readonly center: Vec3;
  readonly rot: Quat;
  readonly half: Vec3;
  /**
   * The LOCAL axis that points through the door — a name, not a direction.
   *
   * <b>Rotate it by this portal's own `rot`, then by the placement.</b> Every portal in the pack declares `"-Z"`, which
   * reads like a constant and is not one: an interior's door back out carries a half turn, so its forward in the
   * asset's frame is `+Z`. Reading the string as a direction gets every entrance right — the exterior's `rot` is the
   * identity — and every exit exactly backwards, so a body could walk into a house and never walk out, with nothing
   * raised anywhere. `ASSET_PIPELINE.md` §5.2 states the rule.
   */
  readonly forward: string;
  readonly target: string;
  readonly target_spawn: string;
  readonly exit_spawn: string;
  /** How a bot reaches the portal, feet height, in the asset's frame (ASSET_FORMAT.md 5.7); absent is none. */
  readonly approach?: readonly Vec3[];
}

export interface SpawnDesc {
  readonly id: string;
  readonly pos: Vec3;
  readonly rot: Quat;
  readonly forward: string;
}

export interface InteractionDesc {
  readonly kind: string;
  readonly pos: Vec3;
  readonly rot: Quat;
  readonly forward: string;
  readonly radius: number | null;
  /** A `mount` interaction: the seat it puts the user in (`seats[].id`). */
  readonly seat?: string;
  /**
   * Usable from any side within this many metres, in plan, of the asset's origin (the middle of its base), instead of
   * `radius` about `pos` (ASSET_FORMAT § 5.5, revision 12). `pos` stays where a bot walks to and a user faces from.
   */
  readonly reach_m?: number;
}

/** Where a rider sits on a vehicle (ASSET_FORMAT § 5.8): the root's position, its turn, and the pose it holds. */
export interface SeatDesc {
  readonly id: string;
  readonly pos: Vec3;
  readonly rot: Quat;
  readonly forward: string;
  readonly pose?: { readonly clip: string; readonly at: 'start' | 'end' };
}

export interface ChildDesc {
  readonly asset: string;
  readonly pos: Vec3;
  readonly rot: Quat;
  /** How a bot reaches the child's interaction, feet height, in this asset's frame (ASSET_FORMAT.md 5.7); absent is none. */
  readonly approach?: readonly Vec3[];
}

export interface ClipDesc {
  readonly name: string;
  readonly duration: number;
  readonly loop: boolean;
}

/** Gameplay roles mapped to clip names; a role may offer several clips (attacks). */
export type RoleMap = Readonly<Partial<Record<string, string | readonly string[]>>>;

/**
 * The clips a rig offers for a gameplay role.
 *
 * <b>One definition, because a role is the ONLY way a clip is addressed.</b> `casual_bald` publishes no `Run` and
 * fills the run role with `Run_Carry`; nothing outside its descriptor knows that, and nothing should. A second copy of
 * this resolution is a second place for a rig's naming to leak into code.
 *
 * @param roles The descriptor's role map.
 * @param role The gameplay role.
 * @returns Its clips, a single name normalised to a one-element list.
 * @throws If the rig declares no clip for the role — deliberately, and where the rig is mounted rather than in a
 * render loop: a character that cannot idle or walk is a catalogue fault, not a frame-time condition.
 */
export function clipsForRole(roles: RoleMap, role: string): readonly string[] {
  const clips = roles[role];
  if (clips === undefined) {
    throw new Error(`no clip for role "${role}"`);
  }

  return typeof clips === 'string' ? [clips] : clips;
}

/**
 * One detail level of a skinned asset: the node that carries it, and what it costs.
 *
 * <b>The node name is a label, not a selector.</b> The published GLBs carry two EMPTY transform nodes also called
 * `Body_LOD1` and `Body_LOD2`, siblings of the armature — residue of the exporter's `regroup_lods` step. The real
 * meshes are leaves under an intermediate node itself named `Body`. Code that picks a level by matching this name
 * against the scene graph finds the decoys; pick by "has vertices and a skeleton" and use this only to know how many
 * levels to expect and what each one costs.
 */
export interface LodDesc {
  readonly node: string;
  readonly triangles: number;
  /**
   * The largest distance, in metres, a point of this level departs from the full-detail surface.
   *
   * Published since catalogue `f131e3e086ab` and absent before it. It is what lets a level be chosen by what the
   * viewer would LOSE rather than by a distance somebody tuned: see `render/asset-lod.ts`. Level 0 states 0.
   */
  readonly error_m?: number;
}

/**
 * A hand weapon (ASSET_FORMAT.md § 5.10): the character clip it attacks with, and where it sits in the hand — in the
 * frame of the character joint `joint`, `pos` in metres with the character's scale removed, `rot` mapping the weapon's
 * frame into the joint's.
 */
export interface WeaponDesc {
  readonly class: string;
  readonly clip: string;
  readonly attach: { readonly joint: string; readonly pos: Vec3; readonly rot: Quat };
}

/** One Mk's look (ASSET_FORMAT.md § 5.11): the factors the item's Mk material takes at that level. Linear colours. */
export interface ItemMkLevel {
  readonly base_color: Vec3;
  readonly emissive: Vec3;
  readonly emissive_strength: number;
}

/**
 * What a player carries, drops or crafts with (ASSET_FORMAT.md § 5.11). Read only by a 3D model of the item, which nothing
 * draws yet: the inventory grid needs the catalogue's icons only.
 */
export interface ItemDesc {
  /** `material`, `core`, `addon`, `part`, `armor`, `potion`, `kit`, `container`; a value not known here is skipped (rule 3). */
  readonly category: string;
  /** Items that drop at Mk I-V: the GLB material `<asset_id>:<material>` takes `levels[mk - 1]`, Mk I first. */
  readonly mk?: { readonly material: string; readonly levels: readonly ItemMkLevel[] };
  /** The crate: the GLB material `<asset_id>:<label>`, a plate whose `TEXCOORD_0` spans 0..1 across it. */
  readonly label?: string;
}

export interface Descriptor {
  readonly schema: number;
  readonly id: string;
  readonly kind: AssetKind;
  readonly frame: string;
  /** Where the asset came from; characters carry the uniform `scale` their pack was published at (§ 5.10 removes it). */
  readonly source?: { readonly scale?: number };
  /** Weapons only (§ 5.10). */
  readonly weapon?: WeaponDesc;
  /** Items only (§ 5.11). */
  readonly item?: ItemDesc;
  /** Characters only: the local axis the model faces ("+Z" for the Quaternius rigs). */
  readonly forward?: string;
  readonly render: {
    readonly glb: string;
    readonly triangles: number;
    readonly materials: readonly string[];
    readonly ao_px?: number;
    readonly skinned?: boolean;
    /** Skinned assets: how many joints the one skin has. 23 for every character in the Quaternius pack. */
    readonly joints?: number;
    /**
     * The detail levels inside this asset's GLB, coarsest LAST — `[{ node: 'Body' }, { node: 'Body_LOD1' }, …]`.
     *
     * <b>Levels are nodes in the one file, not separate assets.</b> All of them are bound to the same skin, which is
     * what lets one baked bone texture drive every level of a character. LOD1 and LOD2 arrive hidden through
     * `KHR_node_visibility`; switching level is a visibility toggle, not a load.
     *
     * <b>Every published character has carried this since catalogue `dd7c11021d4b`, and nothing read it.</b> It was
     * absent from this type while being present in every descriptor on disk — so the client fetched three levels of
     * geometry per character and drew only the finest.
     *
     * <b>And `KHR_node_visibility` is NOT what hides the coarse levels here.</b> Measured against catalogue
     * `f131e3e086ab`, every level of every placed structure arrived enabled and visible: one `tat_house_large` drew
     * 9 428 triangles instead of 5 675, with LOD1 and LOD2 z-fighting inside LOD0, and 40 % of all structure geometry
     * on screen was a coarse level nobody asked for. Whoever draws a multi-level asset owns the choice — for
     * structures that is `render/asset-lod.ts` through `app/static-assets.ts`, and for characters the crowd, which
     * merges each level into a mesh of its own.
     */
    readonly lods?: readonly LodDesc[];
  };
  readonly bounds: { readonly render: readonly [Vec3, Vec3]; readonly collision: readonly [Vec3, Vec3] | null };
  readonly collision: {
    readonly band_m: number | null;
    readonly shapes: readonly ShapeDesc[];
    readonly colliders: readonly ColliderDesc[];
  } | null;
  readonly portals: readonly PortalDesc[];
  readonly spawns: readonly SpawnDesc[];
  readonly interactions: readonly InteractionDesc[];
  /** Vehicles: where riders sit (§ 5.8). */
  readonly seats?: readonly SeatDesc[];
  readonly children: readonly ChildDesc[];
  readonly animations?: { readonly clips: readonly ClipDesc[]; readonly roles: RoleMap };
  readonly foundation_depth?: number;
  /** Interiors: how to get from anywhere in the room to anywhere else, its way out first (§ 5.9). */
  readonly nav?: NavDesc;
}
