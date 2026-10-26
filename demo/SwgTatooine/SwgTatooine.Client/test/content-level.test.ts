import { describe, expect, it } from 'vitest';
import { conColour } from '../src/data/content-level';
import { formatField } from '../src/data/swg-format';

/** Content levels on the client (content-levels.md § 6): a level's con colour against the viewer's CL, and the level as the inspector reads it. */
describe('a con colour', () => {
  it("follows § 14.3's thresholds on the level less the viewer's CL", () => {
    const at = (d: number) => conColour(20 + d, 20);
    expect(at(-10)).toBe('grey');
    expect(at(-9)).toBe('green');
    expect(at(-5)).toBe('green');
    expect(at(-4)).toBe('yellow');
    expect(at(0)).toBe('yellow');
    expect(at(2)).toBe('yellow');
    expect(at(3)).toBe('orange');
    expect(at(5)).toBe('orange');
    expect(at(6)).toBe('red');
  });

  it('is neutral while the viewer has no level to compare with', () => {
    expect(conColour(23, null)).toBeNull();
    expect(conColour(23, 0)).toBeNull();
    expect(conColour(Number.NaN, 10)).toBeNull();
  });
});

describe('a level', () => {
  it('reads as its number in the inspector', () => {
    expect(formatField('Creature', 'level', 23)).toBe('23');
    expect(formatField('CreatureLair', 'level', 1)).toBe('1');
  });
});
