/**
 * Which detail level of a published asset is worth drawing, from the error the publisher measured.
 *
 * <b>The pack states a geometric error in METRES per level</b> (`error_m`, catalogue `f131e3e086ab` onwards): the
 * largest distance a point on that level departs from the full-detail surface. Divided by the distance to the eye and
 * multiplied by the camera's pixels-per-metre, that is an error in SCREEN PIXELS — the only currency in which a
 * threshold means the same thing at any resolution, field of view or distance.
 *
 * <b>CSS pixels, like the crowd's tiers and unlike the terrain's Detail slider.</b> The canvas renders at up to
 * `MAX_DEVICE_RATIO` times its CSS size, and the terrain scales its tolerance by that because its budget is spent on
 * the geometry it actually rasterises. This budget is a judgement about what a VIEWER can see — the same kind of
 * quantity as a 12 px label or an 8 px pick radius, which are CSS pixels by intent — so it is not scaled, and on a
 * HiDPI display a structure may drift up to `MAX_DEVICE_RATIO` rendered pixels. That is the choice; it is written
 * down because the three thresholds in this client look interchangeable and two of them are.
 *
 * So the rule is one line: take the coarsest level whose error still costs less than the tolerance. A level 0 whose
 * error is zero always qualifies, which is what makes the search total.
 *
 * <b>Pure and engine-free.</b> Nothing here knows what a mesh is.
 */

/** A level this asset has not been assigned yet, so no hysteresis applies to its first choice. */
export const NO_LEVEL = -1;

/**
 * How much of the published error the tolerance may drift before a level changes, as a fraction.
 *
 * Without it a structure sitting exactly on a boundary swaps mesh every frame as the camera breathes, which is both
 * visible and a per-frame enable/disable of every one of its primitives.
 */
export const LEVEL_HYSTERESIS = 0.2;

/**
 * The detail level a node name belongs to.
 *
 * The convention is the publisher's: level 0 carries no suffix, the rest carry `_LOD<n>`, possibly before a primitive
 * suffix (`_x_tat_house_large_LOD1_primitive3`).
 *
 * <b>Anchored to the end, because a loose match reads a level out of the asset's own NAME.</b> Babylon labels a
 * placement's meshes with the renderer's own string, which embeds the asset id
 * (`tat_house_large#199550/_x_tat_house_large_LOD2`), and the node name embeds it again. So an asset legitimately
 * called `bunker_LOD3_kit` would be read as level 3 everywhere, at every level, and every one of its meshes would
 * land in the same bucket — taking neither first nor last match out of trouble, since the id appears on both sides.
 * Only the position of the suffix tells them apart. Measured against every name the pack produces:
 * `Body_LOD1_primitive0`, `_x_tat_house_large_LOD2`, `_x_tat_shuttleport_LOD1_primitive8`.
 *
 * @param name The node or mesh name.
 * @returns The level, 0 when the name carries no level suffix.
 */
export function lodLevelOf(name: string): number {
  const m = /_LOD(\d+)(?:_primitive\d+)?$/i.exec(name);
  return m === null ? 0 : Number(m[1]);
}

/**
 * The coarsest level whose screen-space error stays inside the tolerance.
 *
 * @param errors Each level's published error in metres, finest first and NON-DECREASING; `errors[0]` is normally 0.
 * A ladder that does not increase is a publishing fault and this does not detect it — the search returns the highest
 * INDEX that fits, which on a scrambled ladder need not be the cheapest mesh. `groupLevels` refuses one before it
 * reaches here, which is the right place: it is a property of the descriptor, checked once, not per frame.
 * @param pixelsPerMetre The active camera's pixels per metre at one metre.
 * @param distance Metres from the eye to the asset. Clamped away from zero by the caller's own guard below.
 * @param tolerancePx What the viewer is allowed to lose, in screen pixels.
 * @param previous The level drawn last frame, or {@link NO_LEVEL}.
 * @returns The level to draw, always a valid index into `errors`.
 *
 * @remarks
 * <b>Hysteresis is asymmetric on purpose.</b> Entering a COARSER level than the one held is judged against a stricter
 * tolerance and keeping the one held against a looser one, so the swap happens at two different distances depending on
 * which way the camera is moving. Judging both at the same figure is what makes a building flicker when a player
 * stands still on the boundary and the camera bobs.
 */
export function levelForError(
  errors: readonly number[],
  pixelsPerMetre: number,
  distance: number,
  tolerancePx: number,
  previous: number = NO_LEVEL,
): number {
  const d = Math.max(0.01, distance);
  for (let level = errors.length - 1; level > 0; level--) {
    // Coarser than what is drawn: a change the viewer would see as detail disappearing, so it must clear the stricter
    // bar. Same or finer: the looser one, which is what makes the level sticky once it is held.
    const limit = level > previous ? tolerancePx * (1 - LEVEL_HYSTERESIS) : tolerancePx * (1 + LEVEL_HYSTERESIS);
    if (((errors[level] ?? 0) * pixelsPerMetre) / d <= limit) {
      return level;
    }
  }

  return 0;
}
