import type { Descriptor, RoleMap } from '../assets/descriptor';

/**
 * Which character mesh a body wears, and which rig the crowd's animation is baked from.
 *
 * <b>Pure.</b> Babylon enters nowhere here; this decides names and indices from the catalogue and from netIds, and is
 * tested without a scene.
 */

/**
 * The outfit whose clips are baked into the shared bone texture.
 *
 * <b>Named, never derived — and this constant is a bug fix.</b> The crowd spike chose its rig as "the outfit with the
 * most clips", which sounds harmless and is not: 17 of the 19 characters have 17 clips, `Array.sort` is stable, and
 * the catalogue's keys are alphabetical, so the winner was whichever 17-clip id sorted first. That was
 * `blue_soldier_female` — and `blue_soldier_female` is one of the six models the pack gives its own animation keys.
 *
 * Measured by forward kinematics and four-influence skinning against the uncompressed pack sources, worst vertex
 * displacement at publish scale, with a self-check of 0.0 mm:
 *
 * | clip | wrong on | worst vertex | cause |
 * |------|----------|-------------:|-------|
 * | Idle | **all 18 other outfits** | 105–138 mm | 28.6° on `Fist.L` |
 * | Walk | `base_character` | 137 mm | 47.0° on `UpperLeg.R` |
 * | Run | `blue_soldier_male` | **414 mm** | 22.8° on `Abdomen` |
 * | Run | `casual3_male` | 80 mm | 31.0° on `LowerLeg.R` |
 *
 * On `Idle` it is the ODD ONE OUT rather than the representative: the other eighteen agree with each other and all
 * disagree with it. Baking its idle gives eighteen of nineteen agents a wrong arm, and idle is what most of a
 * wandering crowd is doing. 414 mm is a quarter of body height, swung from a key near the root.
 *
 * <b>So the rig is pinned to a CONFORMER, and the choice pays for itself.</b> Against `casual_male`, thirteen of the
 * nineteen outfits are 0.0 mm on every driven role and need no rows of their own; only five diverge at all, in
 * {@link DIVERGENT_CLIPS}. Against `blue_soldier_female` eighteen of them would have needed their own idle. Same
 * correctness either way — a quarter of the texture.
 *
 * Deriving the rig from load order would also mean the whole crowd's animation changed, silently, if the catalogue's
 * key order ever did.
 */
export const RIG_OUTFIT = 'casual_male';

/**
 * Outfits whose own copy of a role's clip differs from {@link RIG_OUTFIT}'s by enough to see.
 *
 * Roles, not clip names, because that is how everything downstream addresses a clip — `casual_bald` has no `Run` and
 * fills the role with `Run_Carry`, and nothing outside its descriptor should know that.
 *
 * <b>Measured, not inferred.</b> Forward kinematics and four-influence skinning over the uncompressed pack sources,
 * twelve phases per clip, worst vertex displacement at publish scale, against `casual_male`. Self-check against itself
 * is 0.0 mm, and **thirteen of the nineteen outfits are 0.0 mm on all three roles** — they are byte-identical
 * animation, which is what makes one shared bake viable at all.
 *
 * | outfit | idle | walk | run |
 * |--------|-----:|-----:|----:|
 * | `base_character` | 0.0 | **137.5** | 0.0 |
 * | `blue_soldier_female` | **124.6** | 0.0 | 0.0 |
 * | `blue_soldier_male` | 0.0 | 0.0 | **413.9** |
 * | `casual3_male` | **44.8** | 0.0 | **80.0** |
 * | `casual_bald` | **45.5** | 0.0 | **79.9** (through `Run_Carry`) |
 *
 * The 45 mm pair would have been missed by reasoning from the pack's own notes, which mention only legs and fists on
 * these two. Re-derive with `skin_diff2.py` against whatever {@link RIG_OUTFIT} becomes; do not hand-edit this table.
 */
export const DIVERGENT_CLIPS: Readonly<Partial<Record<string, readonly string[]>>> = {
  base_character: ['walk'],
  blue_soldier_female: ['idle'],
  blue_soldier_male: ['run'],
  casual3_male: ['idle', 'run'],
  casual_bald: ['idle', 'run'],
};

/** The locomotion roles the crowd drives on every body, every frame. */
export const CROWD_ROLES: readonly string[] = ['idle', 'walk', 'run'];

/**
 * The one-shots the crowd plays: a blow when the server streams one (`Attack`), and death when a body's mode says so.
 *
 * <b>Baked from the rig alone for the characters.</b> {@link DIVERGENT_CLIPS} was measured on locomotion only; nobody has
 * measured these two across the outfits, and an attack held for 0.7 s is forgiving where a looping idle is not. The day
 * one looks wrong on a costume, measure it with `skin_diff2.py` and add it to that table.
 */
export const CROWD_ACTION_ROLES: readonly string[] = ['attack', 'death'];

/**
 * A role map with the locomotion every crowd body is driven through filled in: a creature that cannot run walks, and one
 * that cannot walk hops or idles.
 *
 * <b>Why a creature needs it and a character does not.</b> `AnimationGraph` asks for idle, walk and run on every frame and
 * throws on a missing one — right for the characters, whose pack has all three, and wrong for the creature pack: the snake
 * and the spider have no run, the frog walks by its jump, and the wasp only flies. Filling the gap here, once, keeps the
 * graph strict and the creature descriptors as published.
 *
 * @param roles The descriptor's own roles.
 * @returns The same roles, with `walk` and `run` falling back towards `idle`; the input when nothing was missing.
 */
export function locomotionRoles(roles: RoleMap): RoleMap {
  if (roles.walk !== undefined && roles.run !== undefined) {
    return roles;
  }

  const walk = roles.walk ?? roles.idle;
  const run = roles.run ?? walk;
  return { ...roles, ...(walk === undefined ? {} : { walk }), ...(run === undefined ? {} : { run }) };
}

/**
 * The assets the server dresses its enemies in, which a civilian must never wear: a ninja walking the market reads as an
 * attack about to happen.
 *
 * @param kinds The server's creature templates.
 * @returns Every body id any template names.
 */
export function enemySkins(kinds: readonly { readonly bodies: readonly string[] }[]): ReadonlySet<string> {
  const out = new Set<string>();
  for (const kind of kinds) {
    for (const id of kind.bodies) {
      out.add(id);
    }
  }

  return out;
}

/**
 * The crowd's seated pose on a swoop: not a role any descriptor names, so it is resolved to its clip directly — the clip the
 * avatar holds on the seat (`character-mount.ts`), on its last frame. One copy, the rig's, shared by every outfit: it is
 * not locomotion, so no outfit was measured as diverging on it, and a rider is seen at a swoop's distance.
 */
export const RIDE_ROLE = 'ride';

/** The clip {@link RIDE_ROLE} plays, held at its end. Clip names are identical across the characters (ASSET_FORMAT § 5.8). */
export const RIDE_CLIP = 'SitDown';

/**
 * Whether an outfit needs its own baked copy of a role, rather than the rig's.
 *
 * @param outfit The outfit's catalogue id.
 * @param role The clip role.
 * @returns True when the outfit's own keys differ enough from the rig's to be seen.
 */
export function needsOwnClips(outfit: string, role: string): boolean {
  if (outfit === RIG_OUTFIT) {
    return false;
  }

  return DIVERGENT_CLIPS[outfit]?.includes(role) ?? false;
}

/**
 * The characters a crowd may wear, in catalogue order.
 *
 * <b>By `kind`, never by name.</b> The ids carry an `<outfit>_<sex>` convention that three of them break
 * (`casual_bald`, `ninja_male_hair`, `ninja_sand`), so any rule written against the names is wrong for those three.
 *
 * `base_character` is excluded: it is the pack's bare reference body — two materials, skin and face, no clothes — and
 * it is also the only one missing a clip (`victory`). It stays available as an explicit choice; it is not a costume.
 *
 * @param described Every descriptor the catalogue publishes, by id.
 * @returns The wearable character ids, in the order the catalogue lists them.
 */
export function wearableOutfits(described: ReadonlyMap<string, Descriptor>): string[] {
  const out: string[] = [];
  for (const [id, descriptor] of described) {
    if (descriptor.kind !== 'character' || id === 'base_character') {
      continue;
    }

    // A character with no animations cannot be driven, and one with no locomotion cannot walk. Both are catalogue
    // faults rather than run-time conditions, but a crowd is not the place to discover them.
    //
    // <b>Every role {@link CROWD_ROLES} drives, not just two.</b> `AnimationGraph` throws on a missing role and
    // locomotion asks for one every frame, so an outfit admitted here without `run` takes the render loop down on the
    // first body that breaks into one — inside the frame, which is exactly where that constructor's check says it
    // must never happen. Admitting `idle` and `walk` while the graph demanded three was a disagreement between two
    // files that each looked right alone.
    const roles = descriptor.animations?.roles;
    if (roles === undefined || CROWD_ROLES.some((role) => roles[role] === undefined)) {
      continue;
    }

    out.push(id);
  }

  return out;
}

/**
 * Which outfit a body wears, stable for as long as it is the same body.
 *
 * <b>Derived from the netId, not drawn at random and not stored.</b> A body that changed clothes between frames would
 * be worse than one that never changed them at all, and the packer already works this way for its band state: there is
 * nowhere to keep per-body state that survives the body leaving replication range and coming back.
 *
 * <b>Mixed, not taken modulo.</b> netIds are handed out in runs, so `netId % outfits` would dress a whole spawn wave
 * identically and lay the costumes out in stripes across the map. This is a 32-bit integer hash (the finaliser from
 * MurmurHash3) whose job is only to destroy that correlation.
 *
 * **A netId is not an identity across a `RESET`** — the server renumbers, and the same number is another body. The
 * crowd therefore re-reads this whenever the packer's slot identity changes, exactly as the band state does. A body
 * changing costume across a reset is correct: it is a different body.
 *
 * @param netId The body's replicated id.
 * @param count How many outfits are wearable.
 * @returns An index into the wearable list, or 0 when there is nothing to choose from.
 */
export function outfitFor(netId: number, count: number): number {
  if (count <= 0) {
    return 0;
  }

  let h = netId | 0;
  h ^= h >>> 16;
  h = Math.imul(h, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return (h >>> 0) % count;
}
