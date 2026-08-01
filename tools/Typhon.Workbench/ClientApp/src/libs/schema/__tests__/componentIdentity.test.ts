import { describe, expect, it } from 'vitest';
import {
  captureComponentIdentities,
  classifyComponentTraceRelation,
  declaresComponent,
  traceIdentityOf,
} from '@/libs/schema/componentIdentity';

/**
 * #618 §4.1 — the join key between a database's components and a capture's topology.
 *
 * The bug this replaces was silent: the panel compared its *display* name against declarations holding the CLR full
 * name, matched nothing in either session kind, and rendered null. These lock the identifier down.
 */
describe('component identity (#618)', () => {
  describe('traceIdentityOf', () => {
    it('is the CLR full name, which is what a capture records', () => {
      expect(traceIdentityOf({ typeName: 'Swg.Shard.Transform', fullName: 'Typhon.Samples.Swg.Shard.Transform' }))
        .toBe('Typhon.Samples.Swg.Shard.Transform');
    });

    it('falls back to the schema name only when there is no full name', () => {
      // Not a heuristic: a component whose CLR type could not be resolved records def.Name on BOTH sides, so for that
      // component the schema name genuinely is the recorded identity.
      expect(traceIdentityOf({ typeName: 'Swg.Shard.Transform', fullName: '' })).toBe('Swg.Shard.Transform');
      expect(traceIdentityOf({ typeName: 'Swg.Shard.Transform', fullName: null })).toBe('Swg.Shard.Transform');
    });

    it('is empty for a missing component rather than throwing', () => {
      expect(traceIdentityOf(null)).toBe('');
      expect(traceIdentityOf({})).toBe('');
    });
  });

  describe('declaresComponent', () => {
    const writes = ['Typhon.Samples.Swg.Shard.Transform', 'Game.Health'];

    it('matches the full name a capture actually records', () => {
      expect(declaresComponent(writes, 'Typhon.Samples.Swg.Shard.Transform')).toBe(true);
    });

    it('does NOT match the display name — the exact shape of the bug', () => {
      // `Transform` is what a trace session displays; `Swg.Shard.Transform` is what an open database displays.
      expect(declaresComponent(writes, 'Transform')).toBe(false);
      expect(declaresComponent(writes, 'Swg.Shard.Transform')).toBe(false);
    });

    it('does NOT match a different component that happens to share a leaf', () => {
      // The reason leaf-matching is not an acceptable fallback: Combat.Position and Spatial.Position are different
      // components, and attributing one's systems to the other is worse than showing nothing.
      expect(declaresComponent(['Game.Combat.Position'], 'Game.Spatial.Position')).toBe(false);
    });

    it('is safe on empty and missing inputs', () => {
      expect(declaresComponent(null, 'X')).toBe(false);
      expect(declaresComponent([], 'X')).toBe(false);
      expect(declaresComponent(['X'], '')).toBe(false);
    });
  });

  describe('classifyComponentTraceRelation', () => {
    const known = captureComponentIdentities([{ name: 'Game.Position' }, { name: 'Game.Health' }]);

    it('declared — systems named it', () => {
      expect(classifyComponentTraceRelation('Game.Position', known, true)).toBe('declared');
    });

    it('untouched — the capture knows it and no system declared it', () => {
      // A real finding about the recorded run, not an absence of information.
      expect(classifyComponentTraceRelation('Game.Health', known, false)).toBe('untouched');
    });

    it('absent — the capture never saw it, so nothing can be said', () => {
      expect(classifyComponentTraceRelation('Game.Inventory', known, false)).toBe('absent');
    });

    it('captureComponentIdentities skips blanks', () => {
      expect(captureComponentIdentities([{ name: '' }, { name: null }, { name: 'A' }])).toEqual(new Set(['A']));
      expect(captureComponentIdentities(null).size).toBe(0);
    });
  });
});
