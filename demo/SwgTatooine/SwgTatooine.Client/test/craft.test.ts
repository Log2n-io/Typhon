import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { ceiling, lineValue, parseCraft, parseSchematics, type CraftInput } from '../src/data/craft';

/**
 * C2-1 (crafting-build.md): the client's twin of the server's craft ceilings and line values, against the server's own numbers — the vectors and the
 * schematics `CraftVectorChecks` writes. Same inputs, the same ceiling to the millionth and the same value to the bit.
 */

interface Vector {
  readonly schematic: number;
  readonly inputs: readonly CraftInput[];
  readonly lines: readonly { readonly q: number; readonly value: number; readonly valueEnhanced: number }[];
}

const data = (name: string): unknown => JSON.parse(readFileSync(join(__dirname, 'data', name), 'utf8')) as unknown;

describe('the craft twin', () => {
  const book = parseSchematics(data('schematics.json'));
  const vectors = (data('craft-vectors.json') as { vectors: Vector[] }).vectors;

  it('reads the schematics', () => {
    expect(book).not.toBeNull();
    expect(book?.schematics.length).toBeGreaterThan(30);
  });

  it('computes every vector the server wrote: its ceilings and its values', () => {
    expect(vectors.length).toBeGreaterThan(90);
    for (const v of vectors) {
      const s = book?.schematics.find((x) => x.id === v.schematic);
      expect(s, `schematic ${String(v.schematic)}`).toBeDefined();
      if (s === undefined || book === null) {
        continue;
      }

      s.lines.forEach((line, i) => {
        const q = ceiling(book, s, line, v.inputs);
        expect(q, `${s.name}.${line.name} ceiling`).toBe(v.lines[i].q);
        expect(lineValue(line, q, 600_000, 0), `${s.name}.${line.name} value`).toBe(v.lines[i].value);
        expect(lineValue(line, q, 600_000, 2), `${s.name}.${line.name} enhanced`).toBe(v.lines[i].valueEnhanced);
      });
    }
  });

  it('reads a craft under way, and none', () => {
    expect(parseCraft({ craft: null })).toBeNull();
    const craft = parseCraft({
      craft: {
        id: '7',
        schematic: 69,
        output: 69,
        mk: 3,
        pointsLeft: 4,
        addonMk: 0,
        lines: [{ q: 500_000, f: 600_000, value: 90 }],
      },
    });
    expect(craft?.pointsLeft).toBe(4);
  });
});
