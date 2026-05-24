import { create } from 'zustand';

/**
 * Transient UI state for the `g`-leader focus chord (AC2.3 / PC-8): `armed` is `true` while the chord is
 * waiting for its second key, so the StatusBar can show a "waiting for the second key" hint. Not persisted —
 * it's a sub-second interaction state, driven by {@link createChordHandler}'s `onArmedChange`.
 */
interface KeyChordState {
  armed: boolean;
  setArmed: (armed: boolean) => void;
}

export const useKeyChordStore = create<KeyChordState>()((set) => ({
  armed: false,
  setArmed: (armed) => set({ armed }),
}));
