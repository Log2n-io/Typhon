import { create } from 'zustand';

/**
 * Modal-open state for the Dev Fixture surface when no dockview is available (the empty-workspace /
 * Welcome-screen state). The dock-panel registration only mounts once a session is open — but the *only*
 * useful time to generate a fixture is BEFORE a session exists, which is precisely when the dock host isn't
 * there. This store backs a modal fallback so the panel content stays reachable from any entry point
 * (Welcome button, View menu, palette) regardless of whether the dock is up.
 *
 * Tiny: just an open/close flag + actions. No persistence — the modal is transient.
 */
interface DevFixtureModalState {
  isOpen: boolean;
  open: () => void;
  close: () => void;
  toggle: () => void;
}

export const useDevFixtureModalStore = create<DevFixtureModalState>((set) => ({
  isOpen: false,
  open: () => set({ isOpen: true }),
  close: () => set({ isOpen: false }),
  toggle: () => set((s) => ({ isOpen: !s.isOpen })),
}));
