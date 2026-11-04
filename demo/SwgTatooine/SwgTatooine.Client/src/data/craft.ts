/**
 * Crafting on the client (crafting-build.md C2, crafting-catalog.md § 3.4): the schematics as `GET /swg/schematics` answers them, the craft under way
 * as `GET /swg/craft` does, and the twin of the server's ceilings and line values — what the panel shows before the first click. <b>Never the rolls</b>:
 * the server rolls; the client shows how far a line can go and how far it got.
 *
 * The twin is checked against the server's own numbers (`test/data/craft-vectors.json`, written by `CraftVectorChecks`): same inputs, same ceilings to
 * the millionth, same values to the bit.
 */

/** A slot's kind: a quality material (its Mk matters), a bulk one (any Mk), crafted parts, a core. */
export type SlotKind = 'quality' | 'bulk' | 'part' | 'core';

/** What a term reads of its slot. */
export type TermSource = 'toughness' | 'energy' | 'durability' | 'grade' | 'gradekinetic' | 'gradeenergy';

export interface SchematicSlot {
  readonly kind: SlotKind;
  /** The material, part or core template; 0 for an armour piece's segments, which may be padded or composite. */
  readonly template: number;
  readonly item: string;
  readonly count: number;
}

export interface SchematicTerm {
  readonly slot: number;
  readonly source: TermSource;
  readonly weight: number;
}

export interface SchematicLine {
  readonly name: string;
  readonly min: number;
  readonly max: number;
  /** Lower is better (a delay). */
  readonly lower: boolean;
  /** An add-on raises it past its ceiling. */
  readonly enhanced: boolean;
  readonly terms: readonly SchematicTerm[];
}

export interface Schematic {
  readonly id: number;
  readonly name: string;
  readonly kind: string;
  readonly output: number;
  readonly item: string;
  readonly points: number;
  readonly slots: readonly SchematicSlot[];
  readonly lines: readonly SchematicLine[];
}

export interface SchematicBook {
  readonly schematics: readonly Schematic[];
  /** The materials' attributes by template and Mk (Mk I first): toughness, energy, durability, 0..1. */
  readonly attrs: Readonly<Record<string, readonly (readonly number[])[]>>;
}

/** One slot's input, as the ceilings read it: a material's or a core's Mk; parts' grade and biases, averaged over the slot. */
export interface CraftInput {
  readonly template: number;
  readonly mk: number;
  readonly grade: number;
  readonly biasKinetic: number;
  readonly biasEnergy: number;
}

/** A craft under way, as `GET /swg/craft` answers it; fractions in millionths. */
export interface CraftUnderWay {
  readonly id: string;
  readonly schematic: number;
  readonly output: number;
  readonly mk: number;
  readonly pointsLeft: number;
  readonly addonMk: number;
  readonly lines: readonly { readonly q: number; readonly f: number; readonly value: number }[];
}

/** A ceiling's or a progress's unit, as the server keeps them. */
export const SCALE = 1_000_000;

/** `GET /swg/schematics` checked; `null` for anything else. */
export function parseSchematics(answer: unknown): SchematicBook | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const a = answer as Record<string, unknown>;
  if (!Array.isArray(a.schematics) || typeof a.attrs !== 'object' || a.attrs === null) {
    return null;
  }

  return { schematics: a.schematics as Schematic[], attrs: a.attrs as Record<string, number[][]> };
}

/** `GET /swg/craft` checked: the craft, `null` for none or anything malformed. */
export function parseCraft(answer: unknown): CraftUnderWay | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const c = (answer as Record<string, unknown>).craft;
  if (typeof c !== 'object' || c === null) {
    return null;
  }

  const o = c as Record<string, unknown>;
  return typeof o.schematic === 'number' && typeof o.pointsLeft === 'number' && Array.isArray(o.lines)
    ? (c as CraftUnderWay)
    : null;
}

function attribute(book: SchematicBook, template: number, mk: number, index: number): number {
  if (mk < 1 || mk > 5) {
    return 0;
  }

  // The table may lack a template (bulk only) or a Mk: read as absent rather than trusted.
  const row = book.attrs[String(template)] as readonly (readonly number[] | undefined)[] | undefined;
  return row?.[mk - 1]?.[index] ?? 0;
}

function termValue(book: SchematicBook, s: Schematic, term: SchematicTerm, inputs: readonly CraftInput[]): number {
  const i = inputs[term.slot];
  switch (term.source) {
    case 'toughness':
      return attribute(book, i.template, i.mk, 0);
    case 'energy':
      return attribute(book, i.template, i.mk, 1);
    case 'durability':
      return attribute(book, i.template, i.mk, 2);
    case 'grade':
      return s.slots[term.slot].kind === 'core' ? Math.min(Math.max(i.mk, 0), 5) * 0.2 : i.grade;
    case 'gradekinetic':
      return i.grade * i.biasKinetic;
    case 'gradeenergy':
      return i.grade * i.biasEnergy;
  }
}

/** A line's ceiling, in millionths: its weighted inputs, 0..1 — the server's `Crafting.Ceilings`. A line with no terms (a fixed reach) is 1. */
export function ceiling(book: SchematicBook, s: Schematic, line: SchematicLine, inputs: readonly CraftInput[]): number {
  let q = 1;
  if (line.terms.length > 0) {
    q = 0;
    for (const t of line.terms) {
      q += t.weight * termValue(book, s, t, inputs);
    }
  }

  return Math.round(Math.min(Math.max(q, 0), 1) * SCALE);
}

/** A line's value from its ceiling and progress (millionths) and an add-on's Mk — the server's `Schematics.ValueOf`. */
export function lineValue(line: SchematicLine, q: number, f: number, addonMk: number, enhancePerMk = 0.03): number {
  const span = line.max - line.min;
  const reached = Math.min(Math.max((q / SCALE) * (f / SCALE), 0), 1);
  const extra = line.enhanced ? enhancePerMk * addonMk : 0;
  const share = Math.min(1, reached + extra);
  return line.lower ? line.max - span * share : line.min + span * share;
}
