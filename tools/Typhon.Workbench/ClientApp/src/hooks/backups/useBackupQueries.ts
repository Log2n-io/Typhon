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
 * 10 s only while something runs. A job's start and end re-read the status and the job list; the listing (a quick verify of the store) and the archive
 * are re-read only after a job that can have changed the store, or on an explicit refresh.
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

/** The listing with the restorable set (a quick verify of the store): re-run by an explicit refresh, or after a job that wrote the store. */
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

/** The Refresh button: re-reads everything — status, listing, archive and the job list. */
export function useInvalidateBackups() {
  const queryClient = useQueryClient();
  return useCallback(() => queryClient.invalidateQueries({ queryKey: backupKeys.all }), [queryClient]);
}

/** The jobs that write the store: a new pack, a compacted base, deleted packs, a drill record (its ✓/✗ on the timeline). Verify and restore only read it. */
const STORE_WRITING_KINDS: ReadonlySet<string> = new Set(['create', 'compact', 'retention', 'drill']);

/**
 * What a job's start or end can have changed (`kind` null: a start). Always the status (its running job, the engine's state) and the job list; the
 * listing and the archive only after a job that wrote the store.
 */
export function useInvalidateAfterJob() {
  const queryClient = useQueryClient();
  return useCallback(
    async (kind: string | null) => {
      const reads = [
        queryClient.invalidateQueries({ queryKey: ['backups', 'status'] }),
        queryClient.invalidateQueries({ queryKey: ['backups', 'jobs'] }),
      ];
      if (kind != null && STORE_WRITING_KINDS.has(kind)) {
        reads.push(
          queryClient.invalidateQueries({ queryKey: ['backups', 'list'] }),
          queryClient.invalidateQueries({ queryKey: ['backups', 'archive'] }),
        );
      }
      await Promise.all(reads);
    },
    [queryClient],
  );
}
