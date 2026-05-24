// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import { usePanelHotkeys } from '@/hooks/usePanelHotkeys';

// AC2.3 (PC-8) — panel-scoped hotkeys. Capture-phase, active-panel-gated, with precedence over global keys.

type Api = IDockviewPanelProps['api'];
const api = (isActive: boolean) => ({ isActive }) as unknown as Api;

// Dispatch on a descendant (body) so capture (window) precedes bubble (window). Dispatching on window itself
// would make every window listener fire in registration order, masking the capture/bubble distinction.
function press(key: string, mods: KeyboardEventInit = {}) {
  const e = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...mods });
  document.body.dispatchEvent(e);
  return e;
}

afterEach(cleanup);

describe('usePanelHotkeys', () => {
  it('fires a handler only while the panel is active', () => {
    const fn = vi.fn();
    const { rerender } = renderHook(({ active }) => usePanelHotkeys(api(active), { g: fn }), { initialProps: { active: false } });
    press('g');
    expect(fn).not.toHaveBeenCalled();
    rerender({ active: true });
    press('g');
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('is inert when the api is undefined', () => {
    const fn = vi.fn();
    renderHook(() => usePanelHotkeys(undefined, { g: fn }));
    press('g');
    expect(fn).not.toHaveBeenCalled();
  });

  it('takes precedence over a global bubble listener (capture + stopImmediatePropagation)', () => {
    const panelFn = vi.fn();
    const globalFn = vi.fn();
    window.addEventListener('keydown', globalFn); // global chord-leader analogue (bubble phase)
    renderHook(() => usePanelHotkeys(api(true), { g: panelFn }));
    press('g');
    expect(panelFn).toHaveBeenCalledTimes(1);
    expect(globalFn).not.toHaveBeenCalled();
    window.removeEventListener('keydown', globalFn);
  });

  it('does not consume an unclaimed key (the global leader stays reachable)', () => {
    const panelFn = vi.fn();
    const globalFn = vi.fn();
    window.addEventListener('keydown', globalFn);
    renderHook(() => usePanelHotkeys(api(true), { '[': panelFn })); // claims '[' only, not 'g'
    press('g');
    expect(panelFn).not.toHaveBeenCalled();
    expect(globalFn).toHaveBeenCalledTimes(1); // 'g' reaches the global listener
    window.removeEventListener('keydown', globalFn);
  });

  it('ignores modified keys', () => {
    const fn = vi.fn();
    renderHook(() => usePanelHotkeys(api(true), { g: fn }));
    press('g', { ctrlKey: true });
    expect(fn).not.toHaveBeenCalled();
  });
});
