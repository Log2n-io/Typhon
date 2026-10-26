import type { Descriptor } from '../assets/descriptor';
import { clipsForRole } from '../assets/descriptor';
import {
  CROWD_ACTION_ROLES,
  CROWD_ROLES,
  RIDE_CLIP,
  RIDE_ROLE,
  RIG_OUTFIT,
  WEAPON_CLIPS,
  GET_UP_CLIP,
  locomotionRoles,
  needsOwnClips,
  clipOfRole,
  clipRole,
} from './outfits';

/**
 * Where every crowd clip lives in the one bone-matrix texture, decided before any of it is baked.
 *
 * <b>Pure, and separate from the bake for a reason.</b> What the crowd gets wrong is not the matrix arithmetic, it is
 * WHICH animation an outfit plays — a question of row bookkeeping that is fully decidable from the catalogue. Planning
 * it here means the thing most likely to be wrong is the thing most easily tested, without a GPU, a scene, or a GLB.
 *
 * The atlas is one texture because the material binds one: `crowdVat` is a single sampler, so every clip any outfit
 * might play has to be addressable in it. That costs nothing, because a clip is addressed by an ABSOLUTE START ROW
 * carried per instance — so an outfit playing its own copy of a clip needs no shader change, no extra attribute and no
 * second draw. It points at different rows.
 */

/** 30 Hz: the rate the spike baked at, and fine for locomotion a crossfade already smooths. */
export const BAKE_FPS = 30;

/**
 * Texels per row, per bone. One bone's matrix is four RGBA texels.
 *
 * The spike reserved `(bones + 1) * 4` and never wrote the last matrix — 64 dead bytes on every row, about 6 % of the
 * texture. A row is `bones * 4` here; the shader's largest index is `(bones - 1) * 4 + 3`, which fits exactly.
 */
const TEXELS_PER_BONE = 4;

/** RGBA32F. */
const BYTES_PER_TEXEL = 16;

/** One clip of one outfit, as rows of the atlas. */
export interface ClipSlot {
  /** The row the clip's first frame is written to. What the shader is handed, verbatim. */
  readonly startRow: number;
  readonly frames: number;
  readonly loop: boolean;
  /** The outfit whose GLB these rows were baked FROM — the rig, unless this outfit carries its own keys. */
  readonly source: string;
  /** The clip's name in that GLB. `Run_Carry` where an outfit has no `Run`. */
  readonly clip: string;
}

/** One clip to evaluate and write, in row order. */
export interface BakeStep {
  readonly source: string;
  readonly clip: string;
  readonly startRow: number;
  readonly frames: number;
}

export interface AtlasPlan {
  readonly fps: number;
  readonly bones: number;
  /** Texture width in texels, and height in rows. */
  readonly width: number;
  readonly rows: number;
  readonly bytes: number;
  /** Every clip to bake, in row order; each is loaded once however many outfits share it. */
  readonly bakes: readonly BakeStep[];
  /** Which rows an outfit plays for a role, or `null` when the catalogue cannot serve that role. */
  slotOf(outfit: string, role: string): ClipSlot | null;
}

/**
 * Whose clips an atlas holds: one rig, the members that play from it, and the roles baked.
 *
 * <b>Two shapes, one planner.</b> The characters share one armature across nineteen outfits, so one rig serves them all and
 * only a measured few bake their own copies. A creature is its own rig and its own only member — the rat's 31 joints are
 * not the spider's 39 — so each species gets an atlas, a texture and a material of its own.
 */
export interface AtlasSpec {
  /** The asset whose clips the shared rows are baked from. */
  readonly rig: string;
  /** Which descriptors play from it: by default every character. */
  readonly members: (id: string, descriptor: Descriptor) => boolean;
  /** The roles baked, in row order. */
  readonly roles: readonly string[];
  /** Whether a member bakes its own copy of a role rather than playing the rig's. */
  readonly ownClips: (member: string, role: string) => boolean;
  /** Whether the seated pose is baked (characters ride swoops; creatures do not). */
  readonly ride: boolean;
}

/** The characters' atlas: {@link RIG_OUTFIT}, every character, locomotion and the one-shots, and the seat. */
export const CHARACTER_ATLAS: AtlasSpec = {
  rig: RIG_OUTFIT,
  members: (_id, descriptor) => descriptor.kind === 'character',
  roles: [...CROWD_ROLES, ...CROWD_ACTION_ROLES, ...[...WEAPON_CLIPS, GET_UP_CLIP].map(clipRole)],
  ownClips: needsOwnClips,
  ride: true,
};

/**
 * One creature's atlas: it is its own rig and its only member.
 *
 * @param id The creature's catalogue id.
 * @returns The spec.
 */
export function creatureAtlas(id: string): AtlasSpec {
  return {
    rig: id,
    members: (member) => member === id,
    roles: [...CROWD_ROLES, ...CROWD_ACTION_ROLES],
    ownClips: () => false,
    ride: false,
  };
}

/** How many rows a clip of this length occupies. */
function framesFor(duration: number, fps: number): number {
  return Math.max(1, Math.round(duration * fps));
}

/** The clip an outfit plays for a role, with its duration, or `null` when it declares none. */
function resolve(descriptor: Descriptor, role: string): { clip: string; duration: number; loop: boolean } | null {
  const animations = descriptor.animations;
  if (animations === undefined) {
    return null;
  }

  let clip: string;
  try {
    clip =
      role === RIDE_ROLE ? RIDE_CLIP : (clipOfRole(role) ?? clipsForRole(locomotionRoles(animations.roles), role)[0]);
  } catch {
    return null;
  }

  // The role names a clip the file does not contain: a catalogue fault, and one that has happened — `casual_bald`
  // publishes no `Run` and fills the role with `Run_Carry`, which is exactly why roles are resolved and not assumed.
  const desc = animations.clips.find((c) => c.name === clip);
  return desc === undefined ? null : { clip, duration: desc.duration, loop: desc.loop };
}

/**
 * Lays out the atlas: the rig's clips first, then one copy per outfit that measurably differs from it.
 *
 * <b>Shared by default, copied only where it was measured to matter.</b> Thirteen of the nineteen published characters
 * animate identically to the rig on every role the crowd drives — 0.0 mm of vertex displacement — so they share its
 * rows. The five that do not are listed in {@link DIVERGENT_CLIPS} with the error each one would otherwise show, the
 * worst being 414 mm, a quarter of body height.
 *
 * The result is smaller than the spike's texture while being correct where the spike was not: the spike baked all
 * seventeen clips of one outfit, and the crowd plays three of them.
 *
 * @param described Every descriptor, by catalogue id. Outfits with no usable clip for a role simply get no slot.
 * @param bones How many joints the shared skin has — 23 for every character in this pack.
 * @param fps Bake rate.
 * @returns The plan. Nothing is loaded or evaluated here.
 */
export function planClipAtlas(
  described: ReadonlyMap<string, Descriptor>,
  bones: number,
  fps = BAKE_FPS,
  spec: AtlasSpec = CHARACTER_ATLAS,
): AtlasPlan {
  const bakes: BakeStep[] = [];
  const slots = new Map<string, ClipSlot>();
  const key = (outfit: string, role: string): string => `${outfit}\u0000${role}`;
  /** Clips already baked, by source and name: a creature with no run plays its walk for both roles, from one copy. */
  const baked = new Map<string, ClipSlot>();
  let rows = 0;

  /** Appends a clip's rows once, and returns the slot that addresses them. */
  const append = (source: string, role: string): ClipSlot | null => {
    const descriptor = described.get(source);
    if (descriptor === undefined) {
      return null;
    }

    const resolved = resolve(descriptor, role);
    if (resolved === null) {
      return null;
    }

    const already = baked.get(key(source, resolved.clip));
    if (already !== undefined) {
      return already;
    }

    const frames = framesFor(resolved.duration, fps);
    const slot: ClipSlot = { startRow: rows, frames, loop: resolved.loop, source, clip: resolved.clip };
    bakes.push({ source, clip: resolved.clip, startRow: rows, frames });
    baked.set(key(source, resolved.clip), slot);
    rows += frames;
    return slot;
  };

  // The rig first, so its rows are the ones most instances point at and the ones nearest row 0.
  const rigSlots = new Map<string, ClipSlot>();
  for (const role of spec.roles) {
    const slot = append(spec.rig, role);
    if (slot !== null) {
      rigSlots.set(role, slot);
    }
  }

  const ride = spec.ride ? append(spec.rig, RIDE_ROLE) : null;

  for (const [outfit, descriptor] of described) {
    if (!spec.members(outfit, descriptor)) {
      continue;
    }

    for (const role of spec.roles) {
      if (outfit !== spec.rig && spec.ownClips(outfit, role)) {
        const own = append(outfit, role);
        if (own !== null) {
          slots.set(key(outfit, role), own);
          continue;
        }
        // An outfit that was measured as divergent but cannot serve the role falls back to the rig's rows rather than
        // going unanimated. It is wrong by the millimetres in the table, which is better than standing still.
      }

      const shared = rigSlots.get(role);
      if (shared !== undefined) {
        slots.set(key(outfit, role), shared);
      }
    }

    if (ride !== null) {
      slots.set(key(outfit, RIDE_ROLE), ride);
    }
  }

  const width = bones * TEXELS_PER_BONE;
  return {
    fps,
    bones,
    width,
    rows,
    bytes: width * rows * BYTES_PER_TEXEL,
    bakes,
    slotOf(outfit: string, role: string): ClipSlot | null {
      return slots.get(key(outfit, role)) ?? null;
    },
  };
}
