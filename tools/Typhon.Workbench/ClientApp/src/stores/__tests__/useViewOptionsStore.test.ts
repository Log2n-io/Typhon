import { beforeEach, describe, expect, it } from 'vitest';
import { useViewOptionsStore } from '../useViewOptionsStore';

describe('useViewOptionsStore', () => {
  beforeEach(() => {
    useViewOptionsStore.setState({ showEngineSystems: false });
  });

  it('defaults showEngineSystems to false', () => {
    expect(useViewOptionsStore.getState().showEngineSystems).toBe(false);
  });

  it('setShowEngineSystems toggles the flag', () => {
    useViewOptionsStore.getState().setShowEngineSystems(true);
    expect(useViewOptionsStore.getState().showEngineSystems).toBe(true);

    useViewOptionsStore.getState().setShowEngineSystems(false);
    expect(useViewOptionsStore.getState().showEngineSystems).toBe(false);
  });
});
