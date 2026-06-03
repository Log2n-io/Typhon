import { describe, expect, it } from 'vitest';
import { buildComponentNameMap, leafSegment } from '../componentNames';

describe('buildComponentNameMap', () => {
  it('strips a shared single-segment namespace (the ARPG schema case)', () => {
    const map = buildComponentNameMap(['ARPG.StatusEffects', 'ARPG.Position', 'ARPG.ItemData']);
    expect(map.get('ARPG.StatusEffects')).toBe('StatusEffects');
    expect(map.get('ARPG.Position')).toBe('Position');
    expect(map.get('ARPG.ItemData')).toBe('ItemData');
  });

  it('strips a deep shared namespace down to the leaf (the Workbench fixture case)', () => {
    const map = buildComponentNameMap([
      'Typhon.Workbench.Fixture.Guild',
      'Typhon.Workbench.Fixture.Player',
      'Typhon.Workbench.Fixture.PlayerPosition',
    ]);
    expect(map.get('Typhon.Workbench.Fixture.Guild')).toBe('Guild');
    expect(map.get('Typhon.Workbench.Fixture.Player')).toBe('Player');
    expect(map.get('Typhon.Workbench.Fixture.PlayerPosition')).toBe('PlayerPosition');
  });

  it('is immune to an outlier namespace — others still strip to their leaf', () => {
    // The exact bug a single global common-prefix hit: one stray component left the namespace on everything.
    const map = buildComponentNameMap([
      'Typhon.Workbench.Fixture.Guild',
      'Typhon.Workbench.Fixture.Player',
      'Typhon.Engine.Internal.EntityMap', // outlier in a different subtree
    ]);
    expect(map.get('Typhon.Workbench.Fixture.Guild')).toBe('Guild');
    expect(map.get('Typhon.Workbench.Fixture.Player')).toBe('Player');
    expect(map.get('Typhon.Engine.Internal.EntityMap')).toBe('EntityMap');
  });

  it('keeps one more segment when leaves collide, only for the colliding names', () => {
    const map = buildComponentNameMap(['Game.Combat.Position', 'Game.Spatial.Position', 'Game.Combat.Health']);
    expect(map.get('Game.Combat.Position')).toBe('Combat.Position');
    expect(map.get('Game.Spatial.Position')).toBe('Spatial.Position');
    expect(map.get('Game.Combat.Health')).toBe('Health'); // unique leaf — unaffected by the collision
  });

  it('strips the namespace of a single component too', () => {
    const map = buildComponentNameMap(['ARPG.StatusEffects']);
    expect(map.get('ARPG.StatusEffects')).toBe('StatusEffects');
  });

  it('leaves un-dotted names unchanged', () => {
    const map = buildComponentNameMap(['Position', 'Velocity']);
    expect(map.get('Position')).toBe('Position');
    expect(map.get('Velocity')).toBe('Velocity');
  });

  it('falls back to the full name when two names share every segment but differ in depth', () => {
    // "A.B" leaf is "B"; "A.B.C" leaf is "C" — distinct leaves, both unique.
    const map = buildComponentNameMap(['A.B', 'A.B.C']);
    expect(map.get('A.B')).toBe('B');
    expect(map.get('A.B.C')).toBe('C');
  });

  it('shortens archetype CLR names the same way (reused by the FROM picker / Archetype Inspector)', () => {
    const map = buildComponentNameMap([
      'Typhon.Workbench.Fixtures.PlayerArch',
      'Typhon.Workbench.Fixtures.GuildArch',
      'Typhon.Workbench.Fixtures.ItemArch',
    ]);
    expect(map.get('Typhon.Workbench.Fixtures.PlayerArch')).toBe('PlayerArch');
    expect(map.get('Typhon.Workbench.Fixtures.GuildArch')).toBe('GuildArch');
    expect(map.get('Typhon.Workbench.Fixtures.ItemArch')).toBe('ItemArch');
  });

  it('returns an empty map for no input', () => {
    expect(buildComponentNameMap([]).size).toBe(0);
  });
});

describe('leafSegment', () => {
  it('returns the last dot-segment', () => {
    expect(leafSegment('Typhon.ARPG.Schema.Combat.StatusEffects')).toBe('StatusEffects');
  });

  it('returns an un-dotted name unchanged', () => {
    expect(leafSegment('StatusEffects')).toBe('StatusEffects');
  });
});
