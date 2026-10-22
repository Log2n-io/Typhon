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
