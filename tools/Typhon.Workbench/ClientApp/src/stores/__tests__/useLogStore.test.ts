import { describe, expect, it, beforeEach } from 'vitest';
import { useLogStore, logInfo, logWarn, logError, selectUnseenLevel, selectUnseenCount } from '../useLogStore';

describe('useLogStore', () => {
  beforeEach(() => {
    useLogStore.getState().clear();
    // clear() leaves logsVisible untouched — reset it so each test starts from the default.
    useLogStore.setState({ logsVisible: true });
  });

  it('starts empty', () => {
    expect(useLogStore.getState().entries).toEqual([]);
  });

  it('append adds entries in order with monotonic ids', () => {
    logInfo('a');
    logInfo('b');
    logInfo('c');
    const entries = useLogStore.getState().entries;
    expect(entries.map((e) => e.message)).toEqual(['a', 'b', 'c']);
    const ids = entries.map((e) => e.id);
    expect(ids).toEqual([...ids].sort((x, y) => x - y));
  });

  it('bounds the ring at 500 entries (drops oldest)', () => {
    for (let i = 0; i < 600; i++) {
      logInfo(`msg-${i}`);
    }
    const entries = useLogStore.getState().entries;
    expect(entries).toHaveLength(500);
    expect(entries[0].message).toBe('msg-100');
    expect(entries[499].message).toBe('msg-599');
  });

  it('preserves level and source', () => {
    logInfo('i');
    logWarn('w');
    logError('e');
    const [a, b, c] = useLogStore.getState().entries;
    expect(a.level).toBe('info');
    expect(b.level).toBe('warn');
    expect(c.level).toBe('error');
    expect(a.source).toBe('workbench-ui');
  });

  it('stores structured details alongside the message', () => {
    logInfo('open', { file: 'x.bin', dlls: ['a', 'b'] });
    const entry = useLogStore.getState().entries[0];
    expect(entry.details).toEqual({ file: 'x.bin', dlls: ['a', 'b'] });
  });

  it('clear empties the store', () => {
    logInfo('a');
    logInfo('b');
    useLogStore.getState().clear();
    expect(useLogStore.getState().entries).toEqual([]);
  });
});

describe('useLogStore — unseen-activity tracking', () => {
  const unseen = () => selectUnseenLevel(useLogStore.getState());
  const count = () => selectUnseenCount(useLogStore.getState());

  beforeEach(() => {
    useLogStore.getState().clear();
    useLogStore.setState({ logsVisible: true });
  });

  it('selectUnseenLevel is null on an empty store', () => {
    expect(unseen()).toBeNull();
  });

  it('reports the most critical level among entries appended while hidden', () => {
    useLogStore.getState().setLogsVisible(false);

    logInfo('i');
    expect(unseen()).toBe('info');

    logWarn('w');
    expect(unseen()).toBe('warn');

    logError('e');
    expect(unseen()).toBe('error');
  });

  it('keeps the watermark current while visible — no unseen activity', () => {
    logInfo('a');
    logError('b');
    expect(unseen()).toBeNull();
    expect(useLogStore.getState().lastSeenLogId).toBe(useLogStore.getState().entries[1].id);
  });

  it('becoming visible again clears the dot', () => {
    useLogStore.getState().setLogsVisible(false);
    logError('e');
    expect(unseen()).toBe('error');

    useLogStore.getState().setLogsVisible(true);
    expect(unseen()).toBeNull();
    const entries = useLogStore.getState().entries;
    expect(useLogStore.getState().lastSeenLogId).toBe(entries[entries.length - 1].id);
  });

  it('setLogsVisible(true) on an empty store does not throw and leaves the watermark', () => {
    useLogStore.setState({ lastSeenLogId: 0 });
    expect(() => useLogStore.getState().setLogsVisible(true)).not.toThrow();
    expect(useLogStore.getState().lastSeenLogId).toBe(0);
  });

  it('clear resets the watermark — later hidden appends count from scratch', () => {
    useLogStore.getState().setLogsVisible(false);
    logError('e');
    useLogStore.getState().clear();
    expect(useLogStore.getState().lastSeenLogId).toBe(0);
    expect(unseen()).toBeNull();

    useLogStore.setState({ logsVisible: false });
    logInfo('fresh');
    expect(unseen()).toBe('info');
  });

  it('a later low-severity entry does not lower the reported level', () => {
    useLogStore.getState().setLogsVisible(false);
    logError('e');
    logInfo('i');
    expect(unseen()).toBe('error');
  });

  it('handles ring eviction of unseen entries without throwing', () => {
    useLogStore.getState().setLogsVisible(false);
    for (let i = 0; i < 600; i++) {
      logInfo(`msg-${i}`);
    }
    expect(useLogStore.getState().entries).toHaveLength(500);
    expect(unseen()).toBe('info');
  });

  it('selectUnseenCount is 0 on an empty store', () => {
    expect(count()).toBe(0);
  });

  it('counts entries appended while hidden', () => {
    useLogStore.getState().setLogsVisible(false);
    logInfo('a');
    logWarn('b');
    logError('c');
    expect(count()).toBe(3);
  });

  it('count stays 0 while the panel is visible', () => {
    logInfo('a');
    logInfo('b');
    expect(count()).toBe(0);
  });

  it('counts exactly across ring eviction (more than the 500-entry buffer)', () => {
    useLogStore.getState().setLogsVisible(false);
    for (let i = 0; i < 600; i++) {
      logInfo(`msg-${i}`);
    }
    expect(useLogStore.getState().entries).toHaveLength(500);
    expect(count()).toBe(600);
  });

  it('becoming visible resets the count to 0', () => {
    useLogStore.getState().setLogsVisible(false);
    logInfo('a');
    logError('b');
    expect(count()).toBe(2);
    useLogStore.getState().setLogsVisible(true);
    expect(count()).toBe(0);
  });

  it('clear resets the count to 0', () => {
    useLogStore.getState().setLogsVisible(false);
    logError('e');
    useLogStore.getState().clear();
    expect(count()).toBe(0);
  });
});
