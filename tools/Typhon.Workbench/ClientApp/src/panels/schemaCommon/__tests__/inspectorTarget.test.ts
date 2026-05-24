import { describe, expect, it, vi } from 'vitest';
import { resolveInspectorTarget, commitInspectorTarget, type TargetCandidate } from '@/panels/schemaCommon/inspectorTarget';

// PC-9 cold-open target resolution + deliberate-commit side effects (pure — no rendering, no bus singleton).

const c = (id: string, entityCount: number): TargetCandidate => ({ id, entityCount });

describe('resolveInspectorTarget — PC-9 precedence', () => {
  it('returns null when there are no candidates (→ PC-2 Empty)', () => {
    expect(resolveInspectorTarget([], null)).toBeNull();
    expect(resolveInspectorTarget([], 'x')).toBeNull();
  });

  it('restores the recorded target when still present — not auto (no chip), even if another has more entities', () => {
    const list = [c('800', 10), c('801', 9999)];
    expect(resolveInspectorTarget(list, '800')).toEqual({ id: '800', auto: false });
  });

  it('ignores a recorded target that is no longer in the list and falls to the heuristic', () => {
    const list = [c('800', 10), c('801', 50)];
    expect(resolveInspectorTarget(list, 'gone')).toEqual({ id: '801', auto: true });
  });

  it('auto-picks the most-entities candidate when nothing is recorded', () => {
    const list = [c('800', 10), c('806', 2000), c('801', 50)];
    expect(resolveInspectorTarget(list, null)).toEqual({ id: '806', auto: true });
  });

  it('falls back to the first candidate when all have zero entities (still auto)', () => {
    const list = [c('800', 0), c('801', 0), c('802', 0)];
    expect(resolveInspectorTarget(list, null)).toEqual({ id: '800', auto: true });
  });
});

describe('commitInspectorTarget — deliberate switch side effects', () => {
  it('publishes the bus slot and records the archetype choice (PC-1)', () => {
    const select = vi.fn();
    const savePref = vi.fn();
    commitInspectorTarget({ type: 'archetype', id: '801', prefKey: 'file.typhon', select, savePref });
    expect(select).toHaveBeenCalledWith('archetype', '801');
    expect(savePref).toHaveBeenCalledWith('file.typhon', { archetypeId: '801' });
  });

  it('publishes the bus slot and records the component choice (PC-1)', () => {
    const select = vi.fn();
    const savePref = vi.fn();
    commitInspectorTarget({ type: 'component', id: 'Position', prefKey: 'file.typhon', select, savePref });
    expect(select).toHaveBeenCalledWith('component', 'Position');
    expect(savePref).toHaveBeenCalledWith('file.typhon', { componentType: 'Position' });
  });
});
