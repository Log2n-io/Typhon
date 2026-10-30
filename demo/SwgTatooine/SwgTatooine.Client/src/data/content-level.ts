/**
 * Content levels as a client reads them (claude/design/SwgTatooine/design/content-levels.md): every creature and lair has
 * a level 1-50, sent once on enter, and how it compares with the viewer's own combat level is its "con" colour.
 */

/**
 * The con colours (Loïc, 2026-10-26, superseding crafting-catalog.md § 14.3's five): green is weaker than you, an easy kill;
 * blue is your level; yellow is stronger but manageable; red is too strong.
 */
export type ConColour = 'green' | 'blue' | 'yellow' | 'red';

/** The highest `level − viewerCl` that reads green. */
export const CON_GREEN_MAX = -3;

/** The highest `level − viewerCl` that reads blue; green below, yellow above. */
export const CON_BLUE_MAX = 0;

/**
 * The highest `level − viewerCl` that reads yellow; red above. The CL + 3 rule: a bot takes no mission above it, and the
 * server refuses the take.
 */
export const CON_YELLOW_MAX = 3;

/** How far below the viewer's CL a mission terminal still lists an offer without "Show all". */
export const OFFER_BELOW_MAX = 5;

/**
 * The viewer's combat level as a con colour reads it, from a replicated `level`: `null` for none — the god camera, the crowd,
 * a field not arrived yet.
 */
export function viewerClOf(level: number | null | undefined): number | null {
  return level !== null && level !== undefined && Number.isFinite(level) && level > 0 ? level : null;
}

/**
 * The colour a thing of `level` reads to a viewer of combat level `viewerCl`, on `d = level − viewerCl`: green at
 * {@link CON_GREEN_MAX} and below, blue up to {@link CON_BLUE_MAX}, yellow up to {@link CON_YELLOW_MAX}, red above. `null`
 * — neutral — while the viewer has no level to compare with.
 */
export function conColour(level: number, viewerCl: number | null): ConColour | null {
  if (viewerCl === null || !Number.isFinite(viewerCl) || viewerCl <= 0 || !Number.isFinite(level)) {
    return null;
  }

  const d = level - viewerCl;
  if (d <= CON_GREEN_MAX) {
    return 'green';
  }

  if (d <= CON_BLUE_MAX) {
    return 'blue';
  }

  return d <= CON_YELLOW_MAX ? 'yellow' : 'red';
}

/** The CSS class a level's con colour is drawn with, or `undefined` for neutral. */
export function conClass(level: number, viewerCl: number | null): string | undefined {
  const con = conColour(level, viewerCl);
  return con === null ? undefined : `con-${con}`;
}

/**
 * Whether a mission terminal lists an offer of `level` without "Show all": {@link OFFER_BELOW_MAX} below the viewer's CL up to
 * {@link CON_YELLOW_MAX} above — what is worth taking, and nothing the server would refuse. Everything without a viewer CL.
 */
export function offerInReach(level: number, viewerCl: number | null): boolean {
  if (viewerCl === null) {
    return true;
  }

  return level >= viewerCl - OFFER_BELOW_MAX && level <= viewerCl + CON_YELLOW_MAX;
}

/** An entity's name tag, in parts: the name, and its level — `CL 5`, drawn in `con` — or `''` for none. */
export interface EntityTag {
  readonly name: string;
  readonly level: string;
  readonly con: ConColour | null;
}

/**
 * The tag over an entity (Loïc, 2026-10-26): `name (CL 5)`, the level right after the name. A level of 0 — the crowd, a
 * structure, a field the archetype does not carry — says no level at all.
 */
export function entityTag(name: string, level: number, viewerCl: number | null): EntityTag {
  if (!Number.isFinite(level) || level <= 0) {
    return { name, level: '', con: null };
  }

  return { name, level: `CL ${String(level)}`, con: conColour(level, viewerCl) };
}

/** A tag as plain text: `name (CL 5)`, or `name`. */
export function tagText(tag: EntityTag): string {
  return tag.level === '' ? tag.name : `${tag.name} (${tag.level})`;
}
