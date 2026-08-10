import { useMutation } from '@tanstack/react-query';
import { useCallback, useRef } from 'react';
import {
  postApiIntegrityApply,
  postApiIntegrityPlan,
  postApiIntegrityScan,
} from '@/api/generated/integrity/integrity';
import { normalizeOutcome, normalizePlan, normalizeReport, type ScanDepth } from '@/panels/Integrity/integrityModel';
import { useIntegrityStore } from '@/stores/useIntegrityStore';

/**
 * The three integrity actions, each a mutation rather than a query.
 *
 * Scan *looks* like a query — it is read-only and idempotent — but modelling it as one would mean
 * TanStack Query deciding when it runs: on mount, on window focus, on reconnect. A scan is an explicit
 * operator act with a real cost (a Deep scan reads the whole file), and its result is a diagnosis that
 * a repair plan is then bound to. Refetching it behind the operator's back would silently invalidate
 * a plan they were in the middle of reading. So: it runs when the button is pressed, never otherwise.
 */

/** Runs a scan and stores the normalized report. Supports cancellation — a Deep scan on a large file is slow. */
export function useIntegrityScan() {
  const setReport = useIntegrityStore((s) => s.setReport);
  const abortRef = useRef<AbortController | null>(null);

  const mutation = useMutation({
    mutationFn: async (vars: { path: string; depth: ScanDepth }) => {
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      const res = await postApiIntegrityScan(
        { path: vars.path, depth: vars.depth },
        { signal: controller.signal },
      );
      return normalizeReport(res.data);
    },
    onSuccess: (report) => setReport(report),
  });

  const cancel = useCallback(() => abortRef.current?.abort(), []);
  return { ...mutation, cancel };
}

/**
 * Derives a repair plan. Read-only on the server — it re-scans at Deep depth and describes what it *would*
 * do. Nothing is written, so this is safe to run on a database the operator has not decided to repair.
 */
export function useRepairPlan() {
  const setPlan = useIntegrityStore((s) => s.setPlan);

  return useMutation({
    mutationFn: async (vars: { path: string }) => {
      const res = await postApiIntegrityPlan({ path: vars.path, depth: 'deep' });
      return normalizePlan(res.data);
    },
    onSuccess: (plan) => setPlan(plan),
  });
}

/**
 * Applies a repair — the only mutating call in the feature.
 *
 * `fingerprint` is mandatory and is not defaulted anywhere in this path: the server refuses without it, and
 * a client that could synthesize one would be able to repair against a diagnosis nobody reviewed. `dryRun`
 * results are deliberately *not* written to the store's outcome slot by the caller when they represent a
 * rehearsal — see the panel, which keeps them in local state so a rehearsal can't be mistaken for a receipt.
 */
export function useRepairApply() {
  return useMutation({
    mutationFn: async (vars: {
      path: string;
      fingerprint: string;
      allowLoss: boolean;
      backupFirst: boolean;
      dryRun: boolean;
    }) => {
      const res = await postApiIntegrityApply({
        path: vars.path,
        fingerprint: vars.fingerprint,
        allowLoss: vars.allowLoss,
        backupFirst: vars.backupFirst,
        dryRun: vars.dryRun,
      });
      return normalizeOutcome(res.data);
    },
  });
}
