import { useEffect } from 'react';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { usePaletteStore } from '@/stores/usePaletteStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useTreeVisibilityStore } from '@/stores/useTreeVisibilityStore';
import { useShiftShift } from './useShiftShift';

/**
 * True when the focused element is a text input, textarea, or contenteditable — used to guard
 * plain-letter shortcuts (`g`, `l`) from firing while the user is typing. All modifier-key
 * shortcuts (`Ctrl+K`, `Alt+Shift+T`, etc.) bypass this check because they don't conflict with
 * typical text input.
 */
function isTypingInText(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) return true;
  if (el instanceof HTMLElement && el.isContentEditable) return true;
  return false;
}

export function useKeyboardShortcuts(): void {
  const back = useNavHistoryStore((s) => s.back);
  const forward = useNavHistoryStore((s) => s.forward);
  const toggleTheme = useThemeStore((s) => s.toggle);
  const togglePalette = usePaletteStore((s) => s.toggle);
  const toggleTree = useTreeVisibilityStore((s) => s.toggle);

  useShiftShift(togglePalette);

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'k' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        e.preventDefault();
        togglePalette();
        return;
      }
      if (e.key === 'ArrowLeft' && e.altKey) {
        e.preventDefault();
        back();
        return;
      }
      if (e.key === 'ArrowRight' && e.altKey) {
        e.preventDefault();
        forward();
        return;
      }
      if (e.key === 'T' && e.altKey && e.shiftKey && !e.ctrlKey) {
        e.preventDefault();
        toggleTheme();
        return;
      }
      if (e.key === '/' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        e.preventDefault();
        toggleTree();
        return;
      }

      // Profiler-scoped shortcuts (2f). Plain-letter keys need the typing-guard so they don't
      // fire while the user is typing in the command palette, schema filter, etc. The Ctrl-Home
      // combo bypasses the guard — modifier keys don't conflict with text input.
      if (!e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === 'g' || e.key === 'G')) {
        if (isTypingInText()) return;
        e.preventDefault();
        useProfilerViewStore.getState().toggleGaugeRegion();
        return;
      }
      if (!e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === 'l' || e.key === 'L')) {
        if (isTypingInText()) return;
        e.preventDefault();
        useProfilerViewStore.getState().toggleLegends();
        return;
      }
      if (e.key === 'Home' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        const metadata = useProfilerSessionStore.getState().metadata;
        const gm = metadata?.globalMetrics;
        if (!gm) return;
        const startUs = Number(gm.globalStartUs ?? 0);
        const endUs = Number(gm.globalEndUs ?? 0);
        if (endUs > startUs) {
          e.preventDefault();
          useProfilerViewStore.getState().setViewRange({ startUs, endUs });
        }
        return;
      }
    }

    // Mouse-thumb buttons → nav back/forward. Mouse 3 = back (thumb lower), Mouse 4 = forward
    // (thumb upper). Shift+Mouse 3 is an alt-forward for lefties / keyboard-heavy users who want a
    // mirrored gesture without reaching for the upper thumb button.
    //
    // The in-app navigation runs on `mousedown`. Chrome / Edge dispatch the BROWSER-history
    // back/forward on `mouseup` (and `auxclick`) via a non-standard path that doesn't reliably
    // honour `preventDefault()` from the matching `mousedown` — empirically, with our SPA mounted,
    // the browser navigates AWAY from the Workbench unless we also `preventDefault()` on those
    // events. Belt-and-suspenders: suppress on all three.
    function onMouseDown(e: MouseEvent) {
      if (e.button === 3) {
        e.preventDefault();
        if (e.shiftKey) forward();
        else back();
      } else if (e.button === 4) {
        e.preventDefault();
        forward();
      }
    }

    function suppressNavOnly(e: MouseEvent) {
      if (e.button === 3 || e.button === 4) {
        e.preventDefault();
      }
    }

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('mousedown', onMouseDown);
    window.addEventListener('mouseup', suppressNavOnly);
    window.addEventListener('auxclick', suppressNavOnly);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('mousedown', onMouseDown);
      window.removeEventListener('mouseup', suppressNavOnly);
      window.removeEventListener('auxclick', suppressNavOnly);
    };
  }, [togglePalette, back, forward, toggleTheme, toggleTree]);
}
