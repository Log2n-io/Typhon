import { create } from 'zustand';
import type { Report, RepairOutcome, RepairPlan, ScanDepth } from '@/panels/Integrity/integrityModel';

/**
 * State for the Integrity view — deliberately **path-scoped, not session-scoped**.
 *
 * Every other storage store keys on `sessionId`, because every other storage view introspects a live engine.
 * This one cannot: the case that most justifies the feature is a database that will not open, where there is
 * no session to key on. Keying this store on a session would make the feature unavailable in exactly the
 * situation it exists for — the same argument that made the server API path-based.
 */

/** Which step of scan → plan → apply the view is showing. Derived, never stored, so it cannot disagree. */
export type IntegrityStage = 'idle' | 'scanned' | 'planned' | 'applied';

interface IntegrityState {
  /** Bundle path under examination. Seeded from the open session, the file picker, or a deep link. */
  path: string;
  depth: ScanDepth;
  report: Report | null;
  plan: RepairPlan | null;
  outcome: RepairOutcome | null;
  /** Consent to lossy (Class B) steps. Always starts false — consent is never inherited across plans. */
  allowLoss: boolean;
  /** Copy the bundle before the first mutation. Mirrors the server default. */
  backupFirst: boolean;
  /**
   * Whether the view is showing full-bleed in the no-session shell.
   *
   * `DockHost` only mounts once a session exists (`Shell.tsx`), so a dockview panel cannot serve the case
   * this feature most needs to serve: a database that will not open, where there is no session to host a
   * dock. The view therefore has two homes — a dock panel when a session exists, and the main area when one
   * does not. This flag selects the second.
   */
  standaloneOpen: boolean;

  setPath: (path: string) => void;
  setDepth: (depth: ScanDepth) => void;
  setReport: (report: Report | null) => void;
  setPlan: (plan: RepairPlan | null) => void;
  setOutcome: (outcome: RepairOutcome) => void;
  setAllowLoss: (allow: boolean) => void;
  setBackupFirst: (backup: boolean) => void;
  /** Shows the view full-bleed in the no-session shell, optionally seeding the target path. */
  openStandalone: (path?: string) => void;
  closeStandalone: () => void;
  reset: () => void;
}

const INITIAL = {
  path: '',
  depth: 'standard' as ScanDepth,
  report: null,
  plan: null,
  outcome: null,
  allowLoss: false,
  backupFirst: true,
  standaloneOpen: false,
};

export const useIntegrityStore = create<IntegrityState>()((set) => ({
  ...INITIAL,

  // Changing the target invalidates everything downstream — a report about one database must never be
  // left on screen next to another database's path.
  setPath: (path) => set({ path, report: null, plan: null, outcome: null, allowLoss: false }),

  setDepth: (depth) => set({ depth }),

  // A fresh scan invalidates the plan. The plan is bound to a database fingerprint, so a plan surviving
  // a re-scan is a plan the operator might apply against a diagnosis that has since changed — which is
  // precisely the failure the server's fingerprint check exists to catch. Catch it here too, before the
  // operator has invested any attention in it.
  setReport: (report) => set({ report, plan: null, outcome: null, allowLoss: false }),

  // Consent resets with every new plan. Carrying a checked box across a re-plan would let an operator
  // consent to one loss manifest and apply a different one.
  setPlan: (plan) => set({ plan, outcome: null, allowLoss: false }),

  setOutcome: (outcome) => set({ outcome }),
  setAllowLoss: (allowLoss) => set({ allowLoss }),
  setBackupFirst: (backupFirst) => set({ backupFirst }),

  // A supplied path goes through the same invalidation as setPath; an omitted one keeps whatever was
  // already loaded, so reopening the view returns to the report you were reading.
  openStandalone: (path) =>
    set(path ? { standaloneOpen: true, path, report: null, plan: null, outcome: null, allowLoss: false } : { standaloneOpen: true }),
  closeStandalone: () => set({ standaloneOpen: false }),

  reset: () => set({ ...INITIAL }),
}));

/** Derives the flow stage from what is loaded. Not stored, so it cannot drift from the data. */
export function integrityStage(s: Pick<IntegrityState, 'report' | 'plan' | 'outcome'>): IntegrityStage {
  if (s.outcome) {
    return 'applied';
  }
  if (s.plan) {
    return 'planned';
  }
  return s.report ? 'scanned' : 'idle';
}
