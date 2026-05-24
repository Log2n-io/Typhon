import { useEffect, useRef } from 'react';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { usePaletteStore } from '@/stores/usePaletteStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { toggleViewResourceTree, focusNextPanel, focusPrevPanel, focusChordTarget } from '@/shell/commands/openSchemaBrowser';
import { isTypingInText } from '@/libs/dom/textInput';
import { useKeyChordStore } from '@/stores/useKeyChordStore';
import { createChordHandler, type ChordHandler } from './keyChord';
import { useShiftShift } from './useShiftShift';

export function useKeyboardShortcuts(): void {
  const back = useNavHistoryStore((s) => s.back);
  const forward = useNavHistoryStore((s) => s.forward);
  const toggleTheme = useThemeStore((s) => s.toggle);
  const togglePalette = usePaletteStore((s) => s.toggle);
  useShiftShift(togglePalette);

  // `g`-leader focus chord (PC-8): `g` then c/a/s/d/m focuses Component / Archetype / Schema / Data Browser / File Map.
  // Created once. A panel that claims `g` (the profiler gauge) intercepts it first via capture-phase
  // usePanelHotkeys, so the leader is only free — and the chord only arms — outside such a panel.
  const chordRef = useRef<ChordHandler | null>(null);
  if (chordRef.current === null) {
    chordRef.current = createChordHandler({
      leader: 'g',
      resolve: focusChordTarget,
      isTyping: isTypingInText,
      onArmedChange: (armed) => useKeyChordStore.getState().setArmed(armed),
    });
  }

  useEffect(() => {
    const chord = chordRef.current;
    function onKeyDown(e: KeyboardEvent) {
      if (chord && chord.handle(e)) {
        return;
      }
      if (e.key === 'k' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        e.preventDefault();
        togglePalette();
        return;
      }
      // F6 / Shift+F6 — cycle keyboard focus between dockview panels (PC-8 panel traversal).
      if (e.key === 'F6') {
        e.preventDefault();
        if (e.shiftKey) focusPrevPanel();
        else focusNextPanel();
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
        toggleViewResourceTree();
        return;
      }

      // Profiler timeline jump-to-start. (The former bare `g`/`l` gauge/legend toggles moved to the profiler
      // view as panel-scoped keys — see ProfilerPanel/usePanelHotkeys — so they no longer fire app-wide; `g`
      // is now the global focus-chord leader.) Ctrl-Home bypasses the typing-guard — modifiers don't collide.
      if (e.key === 'Home' && e.ctrlKey && !e.shiftKey && !e.altKey) {
        const metadata = useProfilerSessionStore.getState().metadata;
        const gm = metadata?.globalMetrics;
        if (!gm) return;
        const startUs = Number(gm.globalStartUs ?? 0);
        const endUs = Number(gm.globalEndUs ?? 0);
        if (endUs > startUs) {
          e.preventDefault();
          useProfilerViewStore.getState().commitViewRange({ startUs, endUs });
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
      chord?.cancel();
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('mousedown', onMouseDown);
      window.removeEventListener('mouseup', suppressNavOnly);
      window.removeEventListener('auxclick', suppressNavOnly);
    };
  }, [togglePalette, back, forward, toggleTheme]);
}
