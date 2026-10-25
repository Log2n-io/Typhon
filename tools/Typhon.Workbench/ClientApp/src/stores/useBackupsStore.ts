import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';
import type { RestorePlan, RetentionPlan } from '@/panels/Backups/backupsModel';

/**
 * State for the Backups view (#1417) — operator intent only; server state lives in TanStack Query.
 *
 * Path-scoped like the Integrity view: a bundle (seeded from the open session) and a store (remembered per bundle, PC-1). The invalidation rules live in
 * the setters, so a plan reviewed for one store, point or target is never applied to another: changing the store drops every plan and the job being
 * shown; changing a restore input drops the restore plan; every new plan resets consent.
 */

export type VerifyLevel = 'quick' | 'full' | 'deep';

export type WizardStep = 'point' | 'target' | 'preview' | 'run' | 'result';

export type PointTab = 'point' | 'time' | 'lsn';

/** A restore's inputs, as the wizard collects them. */
export interface RestoreDraft {
  tab: PointTab;
  pointId: number | null;
  /** Local `datetime-local` text; sent as UTC. */
  timeLocal: string;
  lsn: string;
  timelineId: string;
  target: string;
  replace: boolean;
  allowDamaged: boolean;
  allowFormatMismatch: boolean;
}

/** A retention policy, every field the operator's (none defaulted to a limit). Text fields keep what was typed; parsing is the panel's. */
export interface RetentionDraft {
  maxAgeDays: string;
  maxCount: string;
  maxTotalGiB: string;
  minKeep: string;
  compactThreshold: string;
  keepLastDrilled: boolean;
}

const EMPTY_RESTORE: RestoreDraft = {
  tab: 'point',
  pointId: null,
  timeLocal: '',
  lsn: '',
  timelineId: '',
  target: '',
  replace: false,
  allowDamaged: false,
  allowFormatMismatch: false,
};

const EMPTY_RETENTION: RetentionDraft = {
  maxAgeDays: '',
  maxCount: '',
  maxTotalGiB: '',
  minKeep: '',
  compactThreshold: '0',
  keepLastDrilled: false,
};

interface BackupsState {
  bundlePath: string;
  storePath: string;
  /** PC-1: the store last used for each bundle (by lowercase path). */
  storeByBundle: Record<string, string>;
  /** WB-21: the drill scratch directory last used for each store. */
  scratchByStore: Record<string, string>;
  selectedPointId: number | null;
  verifyLevel: VerifyLevel;
  /** The job whose card is shown: a running one (re-attached) or the newest finished one, until dismissed. */
  activeJobId: string | null;
  wizardOpen: boolean;
  wizardStep: WizardStep;
  restore: RestoreDraft;
  restorePlan: RestorePlan | null;
  retentionOpen: boolean;
  retention: RetentionDraft;
  retentionPlan: RetentionPlan | null;
  /** Consent to the retention plan on screen; reset by every new plan. */
  retentionConsent: boolean;
  /** The view's full-bleed home in the no-session shell (Integrity's pattern). */
  standaloneOpen: boolean;

  setBundle: (bundle: string) => void;
  setStore: (store: string) => void;
  selectPoint: (id: number | null) => void;
  setVerifyLevel: (level: VerifyLevel) => void;
  setActiveJob: (jobId: string | null) => void;
  setScratch: (store: string, scratch: string) => void;
  openWizard: (pointId?: number | null) => void;
  closeWizard: () => void;
  setWizardStep: (step: WizardStep) => void;
  updateRestore: (patch: Partial<RestoreDraft>) => void;
  setRestorePlan: (plan: RestorePlan | null) => void;
  openRetention: () => void;
  closeRetention: () => void;
  updateRetention: (patch: Partial<RetentionDraft>) => void;
  setRetentionPlan: (plan: RetentionPlan | null) => void;
  setRetentionConsent: (consent: boolean) => void;
  openStandalone: (bundle?: string) => void;
  closeStandalone: () => void;
}

const key = (path: string) => path.trim().toLowerCase();

/** What changing the store invalidates: everything derived from the old store's contents. */
const STORE_SCOPED = {
  selectedPointId: null,
  activeJobId: null,
  wizardOpen: false,
  wizardStep: 'point' as WizardStep,
  restore: EMPTY_RESTORE,
  restorePlan: null,
  retentionOpen: false,
  retentionPlan: null,
  retentionConsent: false,
};

export const useBackupsStore = create<BackupsState>()(
  persist(
    (set, get) => ({
      bundlePath: '',
      storePath: '',
      storeByBundle: {},
      scratchByStore: {},
      selectedPointId: null,
      verifyLevel: 'full',
      activeJobId: null,
      wizardOpen: false,
      wizardStep: 'point',
      restore: EMPTY_RESTORE,
      restorePlan: null,
      retentionOpen: false,
      retention: EMPTY_RETENTION,
      retentionPlan: null,
      retentionConsent: false,
      standaloneOpen: false,

      // A new bundle brings back the store last used with it (PC-1); with none remembered, the store field keeps what it had.
      setBundle: (bundle) => {
        const remembered = bundle ? get().storeByBundle[key(bundle)] : undefined;
        if (remembered !== undefined && remembered !== get().storePath) {
          set({ bundlePath: bundle, storePath: remembered, ...STORE_SCOPED });
        } else {
          set({ bundlePath: bundle });
        }
      },

      setStore: (store) => {
        const { bundlePath, storeByBundle, storePath } = get();
        const memory = bundlePath ? { storeByBundle: { ...storeByBundle, [key(bundlePath)]: store } } : {};
        set(store === storePath ? { ...memory } : { storePath: store, ...STORE_SCOPED, ...memory });
      },

      selectPoint: (id) => set({ selectedPointId: id }),
      setVerifyLevel: (verifyLevel) => set({ verifyLevel }),
      setActiveJob: (activeJobId) => set({ activeJobId }),
      setScratch: (store, scratch) => set({ scratchByStore: { ...get().scratchByStore, [key(store)]: scratch } }),

      openWizard: (pointId) =>
        set({
          wizardOpen: true,
          wizardStep: 'point',
          restore: { ...EMPTY_RESTORE, pointId: pointId ?? null },
          restorePlan: null,
          retentionOpen: false,
        }),
      closeWizard: () => set({ wizardOpen: false, restorePlan: null, wizardStep: 'point' }),
      setWizardStep: (wizardStep) => set({ wizardStep }),

      // Any change to what would be restored, or where, or how, drops the plan: the preview must be of these inputs.
      updateRestore: (patch) =>
        set((s) => ({
          restore: { ...s.restore, ...patch },
          restorePlan: null,
          wizardStep: s.wizardStep === 'preview' || s.wizardStep === 'run' ? 'target' : s.wizardStep,
        })),
      setRestorePlan: (restorePlan) => set({ restorePlan }),

      openRetention: () =>
        set({ retentionOpen: true, retentionPlan: null, retentionConsent: false, wizardOpen: false }),
      closeRetention: () => set({ retentionOpen: false, retentionPlan: null, retentionConsent: false }),
      // A changed policy is another plan: the one on screen is dropped, consent with it.
      updateRetention: (patch) =>
        set((s) => ({ retention: { ...s.retention, ...patch }, retentionPlan: null, retentionConsent: false })),
      setRetentionPlan: (retentionPlan) => set({ retentionPlan, retentionConsent: false }),
      setRetentionConsent: (retentionConsent) => set({ retentionConsent }),

      openStandalone: (bundle) => {
        if (bundle) {
          get().setBundle(bundle);
        }
        set({ standaloneOpen: true });
      },
      closeStandalone: () => set({ standaloneOpen: false }),
    }),
    {
      name: 'pref:backups',
      storage: safeStorage,
      // Only the preferences persist (PC-1): the paths' memory and the verify level. Plans, consent and jobs never outlive the tab.
      partialize: (s) => ({
        storeByBundle: s.storeByBundle,
        scratchByStore: s.scratchByStore,
        verifyLevel: s.verifyLevel,
      }),
    },
  ),
);

/** The scratch directory remembered for a store, else the server's default (WB-21: the Workbench's per-user state, never inside a store). */
export function scratchFor(store: string, remembered: Record<string, string>, serverDefault: string | null): string {
  return remembered[key(store)] || serverDefault || '';
}
