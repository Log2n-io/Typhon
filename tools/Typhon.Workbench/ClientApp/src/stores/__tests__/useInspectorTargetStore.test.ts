import { beforeEach, describe, expect, it } from 'vitest';
import { useInspectorTargetStore, inspectorTargetKey } from '@/stores/useInspectorTargetStore';

// PC-1 per-file last-viewed inspector target store + its scope key.

beforeEach(() => {
  useInspectorTargetStore.setState({ byKey: {} });
});

describe('useInspectorTargetStore', () => {
  it('saves and merges archetype + component picks under the same file key', () => {
    const { save } = useInspectorTargetStore.getState();
    save('file.typhon', { archetypeId: '801' });
    save('file.typhon', { componentType: 'Position' });
    expect(useInspectorTargetStore.getState().byKey['file.typhon']).toEqual({
      archetypeId: '801',
      componentType: 'Position',
    });
  });

  it('keeps targets isolated per file key', () => {
    const { save } = useInspectorTargetStore.getState();
    save('a.typhon', { archetypeId: '1' });
    save('b.typhon', { archetypeId: '2' });
    expect(useInspectorTargetStore.getState().byKey['a.typhon']?.archetypeId).toBe('1');
    expect(useInspectorTargetStore.getState().byKey['b.typhon']?.archetypeId).toBe('2');
  });
});

describe('inspectorTargetKey — PC-1 scope precedence', () => {
  it('prefers the file path', () => {
    expect(inspectorTargetKey('file.typhon', 'sess-1', 'open')).toBe('file.typhon');
  });
  it('falls back to the session id when there is no file', () => {
    expect(inspectorTargetKey(null, 'sess-1', 'trace')).toBe('sess-1');
  });
  it('falls back to the session kind when neither file nor session is known', () => {
    expect(inspectorTargetKey(null, null, 'attach')).toBe('attach');
  });
});
