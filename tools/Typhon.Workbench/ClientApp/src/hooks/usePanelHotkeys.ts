import { useEffect, useRef } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { isTypingInText } from '@/libs/dom/textInput';

type PanelApi = IDockviewPanelProps['api'];

/**
 * Register panel-scoped keyboard shortcuts (PC-8). A keydown listener fires only while this dock panel is the
 * **active** panel, so the key is "panel-scoped" no matter how focus arrived — a click, F6, or a focus chord
 * (which focuses the group's content container, not a descendant of the panel root, so a root `onKeyDown` would
 * miss it). Plain-letter keys are guarded against text inputs.
 *
 * Registered in the **capture phase** and a handled key is `stopImmediatePropagation`-d, so a focused panel's
 * key takes precedence over a global shortcut on the same key — e.g. the profiler's `g` (gauge) wins over the
 * global `g` chord leader while a profiler view is focused, and the leader stays free everywhere else.
 *
 * `handlers` maps `KeyboardEvent.key` → callback. `api` may be `undefined` (e.g. a panel rendered outside a
 * dockview in tests) — then the hook is inert.
 */
export function usePanelHotkeys(api: PanelApi | undefined, handlers: Record<string, (e: KeyboardEvent) => void>): void {
  const handlersRef = useRef(handlers);
  handlersRef.current = handlers;

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (!api?.isActive) {
        return;
      }
      if (e.ctrlKey || e.altKey || e.metaKey) {
        return;
      }
      const fn = handlersRef.current[e.key];
      if (!fn) {
        return;
      }
      if (isTypingInText()) {
        return;
      }
      e.preventDefault();
      e.stopImmediatePropagation();
      fn(e);
    }
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [api]);
}
