import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import {
  getApiBackupsJobs,
  postApiBackupsArchive,
  postApiBackupsList,
  postApiBackupsStatus,
} from '@/api/generated/backups/backups';
import {
  normalizeArchive,
  normalizeJob,
  normalizeListing,
  normalizeStatus,
  type ArchiveTimeline,
  type Job,
  type Listing,
  type Status,
} from '@/panels/Backups/backupsModel';

/**
 * The Backups view's reads (#1417 §4.2): status, listing, archive and the store's jobs, as TanStack queries keyed on the store (and bundle). They read
 * headers and footers only, so they are cheap — but never refetched on focus (a store on a slow share would stall every tab switch); they poll every
 * 10 s only while something runs, and are invalidated when a job ends.
 */

const POLL_MS = 10_000;

export const backupKeys = {
  all: ['backups'] as const,
  status: (store: string, bundle: string) => ['backups', 'status', store, bundle] as const,
  listing: (store: string) => ['backups', 'list', store] as const,
  archive: (store: string) => ['backups', 'archive', store] as const,
  jobs: (store: string) => ['backups', 'jobs', store] as const,
};

export function useBackupStatus(store: string, bundle: string, running: boolean) {
  return useQuery<Status>({
    queryKey: backupKeys.status(store, bundle),
    queryFn: async () => normalizeStatus((await postApiBackupsStatus({ store, bundle: bundle || null })).data),
    enabled: store.length > 0,
    refetchOnWindowFocus: false,
    refetchInterval: (q) => (running || q.state.data?.engine?.state === 'Running' ? POLL_MS : false),
  });
}

/** The listing with the restorable set (a quick verify of the store): an explicit refresh re-runs it, nothing else does. */
export function useBackupListing(store: string, enabled: boolean) {
  return useQuery<Listing>({
    queryKey: backupKeys.listing(store),
    queryFn: async () => normalizeListing((await postApiBackupsList({ store, restorable: true })).data),
    enabled: enabled && store.length > 0,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

export function useBackupArchive(store: string, enabled: boolean) {
  return useQuery<ArchiveTimeline[]>({
    queryKey: backupKeys.archive(store),
    queryFn: async () => normalizeArchive((await postApiBackupsArchive({ store })).data),
    enabled: enabled && store.length > 0,
    refetchOnWindowFocus: false,
    retry: false,
  });
}

/** The store's jobs, newest first — the re-attach path: a running one is followed, the newest finished one's result shown. */
export function useBackupJobs(store: string) {
  return useQuery<Job[]>({
    queryKey: backupKeys.jobs(store),
    queryFn: async () => ((await getApiBackupsJobs({ store })).data ?? []).map(normalizeJob),
    enabled: store.length > 0,
    refetchOnWindowFocus: false,
  });
}

/** Re-reads everything a job can change: status, listing, archive and the job list of the store. */
export function useInvalidateBackups() {
  const queryClient = useQueryClient();
  return useCallback(() => queryClient.invalidateQueries({ queryKey: backupKeys.all }), [queryClient]);
}
