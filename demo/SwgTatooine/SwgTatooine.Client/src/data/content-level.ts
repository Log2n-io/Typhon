/**
 * Content levels as a client reads them (claude/design/SwgTatooine/design/content-levels.md): every creature and lair has
 * a level 1-50, sent once on enter, and how it compares with the viewer's own combat level is its "con" colour.
 */

/** The con colours, from trivial to deadly (crafting-catalog.md § 14.3). */
export type ConColour = 'grey' | 'green' | 'yellow' | 'orange' | 'red';

/**
 * The colour a thing of `level` reads to a viewer of combat level `viewerCl`, on `level − viewerCl`: grey at −10 and
 * below, green at −5 and below, red at +6 and above, orange at +3 and above, yellow between. `null` — neutral — while the
 * viewer has no level to compare with: the character's CL is not replicated yet.
 */
export function conColour(level: number, viewerCl: number | null): ConColour | null {
  if (viewerCl === null || !Number.isFinite(viewerCl) || viewerCl <= 0 || !Number.isFinite(level)) {
    return null;
  }

  const d = level - viewerCl;
  if (d <= -10) {
    return 'grey';
  }

  if (d <= -5) {
    return 'green';
  }

  if (d >= 6) {
    return 'red';
  }

  return d >= 3 ? 'orange' : 'yellow';
}
