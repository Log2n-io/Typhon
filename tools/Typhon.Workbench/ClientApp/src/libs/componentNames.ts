/**
 * "Smart" component-name display labels for the Query Console.
 *
 * Component type names in Typhon are namespace-qualified — `Typhon.Workbench.Fixture.Guild`, `ARPG.StatusEffects`,
 * `Game.Combat.Position`. The leading namespace segments usually carry no discrimination value, so we shorten each
 * name to the **shortest dot-suffix (counting from the leaf) that is still unique across the whole set**:
 *
 *   Typhon.Workbench.Fixture.Guild  → Guild
 *   Typhon.Workbench.Fixture.Player → Player
 *   Game.Combat.Position ┐ leaf "Position" collides, so each keeps one more segment:
 *   Game.Spatial.Position┘ → Combat.Position / Spatial.Position
 *
 * Why per-name, not a single global common-prefix strip: a global prefix is only as long as the *shortest* shared
 * run across the ENTIRE set, so one outlier in a different namespace (an engine/system component surfaced alongside
 * the user's schema, say) collapses the strippable prefix and leaves the namespace on everything else. Deciding
 * per-name removes that coupling — an outlier can't affect how any other component renders.
 *
 * Guarantees:
 *  - Every label keeps at least its final segment.
 *  - No two components ever produce the same label (the suffix grows until it's unique).
 *  - The map is keyed by the *exact* input string, so callers look up by the same name they hold.
 *  - Stripping is purely cosmetic — the original name stays the identity used for the spec / DSL / parser.
 */
export function buildComponentNameMap(names: readonly string[]): Map<string, string> {
  const map = new Map<string, string>();
  if (names.length === 0) return map;

  const segments = names.map((n) => n.split('.'));

  for (let i = 0; i < names.length; i++) {
    const segs = segments[i];
    // Grow the suffix from the leaf until no *other* name shares the same tail, capped at the full name.
    let take = 1;
    while (take < segs.length) {
      const candidate = suffix(segs, take);
      let collides = false;
      for (let j = 0; j < names.length; j++) {
        if (j === i) continue;
        const other = segments[j];
        if (other.length >= take && suffix(other, take) === candidate) {
          collides = true;
          break;
        }
      }
      if (!collides) break;
      take++;
    }
    map.set(names[i], suffix(segs, take));
  }
  return map;
}

/** The last `count` dot-segments of an already-split name, re-joined. */
function suffix(segs: readonly string[], count: number): string {
  return segs.slice(segs.length - count).join('.');
}

/** Last dot-segment of a name — the fallback label when a name isn't part of the smart map. */
export function leafSegment(name: string): string {
  const i = name.lastIndexOf('.');
  return i === -1 ? name : name.slice(i + 1);
}
