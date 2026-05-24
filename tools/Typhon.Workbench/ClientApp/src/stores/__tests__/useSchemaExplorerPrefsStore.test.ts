import { beforeEach, describe, expect, it } from 'vitest';
import { useSchemaExplorerPrefsStore } from '@/stores/useSchemaExplorerPrefsStore';

// AC2.16 (PC-1) — the Schema Explorer's last mode is recorded + restored per database file.

describe('useSchemaExplorerPrefsStore (PC-1 / AC2.16)', () => {
  beforeEach(() => useSchemaExplorerPrefsStore.setState({ modeByFile: {} }));

  it('records and restores the last mode for a file', () => {
    useSchemaExplorerPrefsStore.getState().setMode('demo.typhon', 'types');
    expect(useSchemaExplorerPrefsStore.getState().modeByFile['demo.typhon']).toBe('types');
  });

  it('keeps each file’s mode independent', () => {
    const { setMode } = useSchemaExplorerPrefsStore.getState();
    setMode('a.typhon', 'types');
    setMode('b.typhon', 'archetypes');
    const { modeByFile } = useSchemaExplorerPrefsStore.getState();
    expect(modeByFile['a.typhon']).toBe('types');
    expect(modeByFile['b.typhon']).toBe('archetypes');
  });

  it('overwrites the saved mode for the same file', () => {
    const { setMode } = useSchemaExplorerPrefsStore.getState();
    setMode('a.typhon', 'types');
    setMode('a.typhon', 'archetypes');
    expect(useSchemaExplorerPrefsStore.getState().modeByFile['a.typhon']).toBe('archetypes');
  });
});
