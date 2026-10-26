import { describe, expect, it } from 'vitest';
import { conClass, conColour, entityTag, offerInReach, tagText, viewerClOf } from '../src/data/content-level';
import { formatField } from '../src/data/swg-format';

/** Content levels on the client (content-levels.md § 6): a level's con colour against the viewer's CL, and the level as the inspector reads it. */
describe('a con colour', () => {
  it("is green, blue, yellow or red on the level less the viewer's CL (Loïc, 2026-10-26)", () => {
    const at = (d: number) => conColour(20 + d, 20);
    expect(at(-19)).toBe('green');
    expect(at(-3)).toBe('green');
    expect(at(-2)).toBe('blue');
    expect(at(0)).toBe('blue');
    expect(at(1)).toBe('yellow');
    expect(at(3)).toBe('yellow');
    expect(at(4)).toBe('red');
    expect(at(30)).toBe('red');
  });

  it('is neutral while the viewer has no level to compare with', () => {
    expect(conColour(23, null)).toBeNull();
    expect(conColour(23, 0)).toBeNull();
    expect(conColour(Number.NaN, 10)).toBeNull();
    expect(conClass(23, null)).toBeUndefined();
  });

  it('is the class the inspector, the terminal and the journal draw a level with', () => {
    expect(conClass(23, 20)).toBe('con-yellow');
    expect(conClass(20, 20)).toBe('con-blue');
  });
});

describe("the viewer's CL", () => {
  it('is the replicated level, or none for the god camera and a field not arrived', () => {
    expect(viewerClOf(5)).toBe(5);
    expect(viewerClOf(0)).toBeNull();
    expect(viewerClOf(Number.NaN)).toBeNull();
    expect(viewerClOf(undefined)).toBeNull();
    expect(viewerClOf(null)).toBeNull();
  });
});

describe('a name tag', () => {
  it("reads the level right after the name, in the con colour for the viewer's CL", () => {
    const creature = entityTag('Krayt dragon', 23, 20);
    expect(tagText(creature)).toBe('Krayt dragon (CL 23)');
    expect(creature.level).toBe('CL 23');
    expect(creature.con).toBe('yellow');

    const player = entityTag('Kira', 5, 5);
    expect(tagText(player)).toBe('Kira (CL 5)');
    expect(player.con).toBe('blue');
  });

  it('says no level for the crowd (level 0) or a thing without one', () => {
    expect(tagText(entityTag('Citizen #4', 0, 5))).toBe('Citizen #4');
    expect(entityTag('Citizen #4', 0, 5).con).toBeNull();
    expect(tagText(entityTag('Bazaar', Number.NaN, 5))).toBe('Bazaar');
  });

  it('keeps its level uncoloured without a viewer CL', () => {
    const tag = entityTag('Rat', 3, null);
    expect(tagText(tag)).toBe('Rat (CL 3)');
    expect(tag.con).toBeNull();
  });
});

describe('an offer within reach', () => {
  it("runs from five levels below the viewer's CL to three above, and is everything without one", () => {
    expect(offerInReach(0, 5)).toBe(true);
    expect(offerInReach(8, 5)).toBe(true);
    expect(offerInReach(9, 5)).toBe(false);
    expect(offerInReach(4, 10)).toBe(false);
    expect(offerInReach(5, 10)).toBe(true);
    expect(offerInReach(50, null)).toBe(true);
  });
});

describe('a level', () => {
  it('reads as its number in the inspector', () => {
    expect(formatField('Creature', 'level', 23)).toBe('23');
    expect(formatField('CreatureLair', 'level', 1)).toBe('1');
  });
});
