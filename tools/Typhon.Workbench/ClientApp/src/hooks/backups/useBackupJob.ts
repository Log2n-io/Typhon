import { useEffect, useMemo, useState } from 'react';
import { FetchError } from '@/api/client';
import { getApiBackupsJobsJobId } from '@/api/generated/backups/backups';
import type { BackupJobDto } from '@/api/generated/model';
import { useEventSource } from '@/hooks/streams/useEventSource';
import { isTerminal, normalizeJob, type Job } from '@/panels/Backups/backupsModel';
import { useInvalidateBackups } from './useBackupQueries';

/**
 * Follows one backup job (#1417 §3.5): its snapshot first (the re-attach path — a reopened tab), then its SSE stream until a terminal frame. The URL
 * turns null once the job is terminal: `useEventSource` reconnects on every close, and the server closes the stream after the terminal frame.
 * `lost` is a 404 — the job is no longer kept (an hour after it ended, or the Workbench restarted).
 */
export function useBackupJob(jobId: string | null): { job: Job | null; lost: boolean } {
  const [job, setJob] = useState<Job | null>(null);
  const [lost, setLost] = useState(false);
  const invalidate = useInvalidateBackups();

  useEffect(() => {
    setJob(null);
    setLost(false);
    if (!jobId) {
      return;
    }
    let cancelled = false;
    getApiBackupsJobsJobId(jobId)
      .then((r) => {
        if (!cancelled && r.data) {
          setJob(normalizeJob(r.data));
        }
      })
      .catch((e: unknown) => {
        if (!cancelled && e instanceof FetchError && e.status === 404) {
          setLost(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [jobId]);

  const listeners = useMemo(() => {
    const on = (dto: BackupJobDto) => {
      const next = normalizeJob(dto);
      setJob(next);
      if (isTerminal(next.state)) {
        void invalidate();
      }
    };
    return { progress: on, done: on, error: on, cancelled: on };
  }, [invalidate]);

  const follow = !!jobId && !lost && !(job && job.jobId === jobId && isTerminal(job.state));
  useEventSource(follow ? `/api/backups/jobs/${jobId}/stream` : null, listeners);
  return { job: job && job.jobId === jobId ? job : null, lost };
}
