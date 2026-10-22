import type { Quat, Vec3 } from '../math/vec';

/**
 * The published asset contract, schema 1 (ASSET_PIPELINE.md § 5). Everything here is in the asset's **glTF frame**:
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

export interface Catalogue {
  readonly schema: number;
  readonly version: string;
  readonly assets: Readonly<Partial<Record<string, CatalogueEntry>>>;
  readonly textures: readonly string[];
}

export type AssetKind = 'building' | 'interior' | 'interactable' | 'prop' | 'vehicle' | 'character';

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

export interface PortalDesc {
  readonly id: string;
  readonly center: Vec3;
  readonly rot: Quat;
  readonly half: Vec3;
  /** The local axis that points through the door, the way a body crosses it. Always "-Z" in schema 1. */
  readonly forward: string;
  readonly target: string;
  readonly target_spawn: string;
  readonly exit_spawn: string;
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
}

export interface ChildDesc {
  readonly asset: string;
  readonly pos: Vec3;
  readonly rot: Quat;
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

export interface Descriptor {
  readonly schema: number;
  readonly id: string;
  readonly kind: AssetKind;
  readonly frame: string;
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
  readonly children: readonly ChildDesc[];
  readonly animations?: { readonly clips: readonly ClipDesc[]; readonly roles: RoleMap };
  readonly foundation_depth?: number;
}
