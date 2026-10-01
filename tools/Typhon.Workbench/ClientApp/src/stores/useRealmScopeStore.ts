import { create } from 'zustand';

/**
 * The realm scope — the third global scope, beside the time window and the revision (IA § 3.4, #1083).
 *
 * **A scope is not a selection, and the two are deliberately separate stores.** Selecting realm 7 in the navigator
 * makes it the Inspector's subject: you are *looking at* it. Scoping to realm 7 narrows every pillar to it: you are
 * *working in* it. Conflating them would mean clicking a realm to read its cell size silently re-pointed the Query
 * Console at it — the kind of action-at-a-distance the context bar exists to make visible instead.
 *
 * **Null is not realm 0.** Unscoped means "no realm named", and every panel must behave exactly as it did before this
 * store existed. That is asserted rather than assumed: a scope that silently defaulted to realm 0 would reintroduce
 * WB-06's bug at the UI layer, one level above the compiler that just stopped doing it.
 */
interface RealmScopeState {
  /** The realm every pillar narrows to, or `null` for unscoped. */
  realm: number | null;
  /**
   * Whether the scope is applied. Unlinking keeps the chosen realm visible but stops it narrowing anything — the same
   * affordance the time window has, and for the same reason: you sometimes want to see the coordinate without moving
   * the panels under study.
   */
  linked: boolean;
  setRealm: (realm: number | null) => void;
  setLinked: (linked: boolean) => void;
  clear: () => void;
}

export const useRealmScopeStore = create<RealmScopeState>((set) => ({
  realm: null,
  linked: true,
  setRealm: (realm) => set((s) => (s.realm === realm ? s : { realm })),
  setLinked: (linked) => set((s) => (s.linked === linked ? s : { linked })),
  clear: () => set({ realm: null, linked: true }),
}));

/**
 * The realm a panel should narrow to right now, or `null` for "do what you did before".
 *
 * The one place the `linked` toggle is honoured, so no panel has to remember to check it — a panel that read `realm`
 * directly would keep narrowing after the user unlinked, which reads as the toggle being broken.
 */
export function activeRealmScope(state: Pick<RealmScopeState, 'realm' | 'linked'>): number | null {
  return state.linked ? state.realm : null;
}
