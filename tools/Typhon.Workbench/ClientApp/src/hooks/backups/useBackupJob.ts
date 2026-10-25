import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FetchError } from '@/api/client';
import { getApiBackupsJobsJobId } from '@/api/generated/backups/backups';
import type { BackupJobDto } from '@/api/generated/model';
import { useEventSource } from '@/hooks/streams/useEventSource';
import { isTerminal, normalizeJob, type Job } from '@/panels/Backups/backupsModel';
import { useInvalidateAfterJob } from './useBackupQueries';

/**
 * Follows one backup job (#1417 §3.5): its snapshot first (the re-attach path — a reopened tab), then its SSE stream until a terminal frame. The URL
 * turns null once the job is terminal: `useEventSource` reconnects on every close, and the server closes the stream after the terminal frame.
 *
 * A stream that drops re-reads the snapshot: a Workbench restarted mid-job no longer knows the job, and the stream would otherwise reconnect every 3 s
 * forever with the card stuck on "running". `lost` is a 404 — the job is no longer kept (an hour after it ended, or the Workbench restarted) — and
 * ends the following.
 */
export function useBackupJob(jobId: string | null): { job: Job | null; lost: boolean } {
  const [job, setJob] = useState<Job | null>(null);
  const [lost, setLost] = useState(false);
  const invalidateAfter = useInvalidateAfterJob();
  const invalidateRef = useRef(invalidateAfter);
  invalidateRef.current = invalidateAfter;
  const current = useRef(jobId);
  current.current = jobId;

  // `afterDrop`: the job may have ended while the stream was down, so its end was never streamed — what it changed is re-read here instead.
  const read = useCallback((id: string, afterDrop: boolean) => {
    getApiBackupsJobsJobId(id)
      .then((r) => {
        if (current.current === id && r.data) {
          const next = normalizeJob(r.data);
          setJob(next);
          if (afterDrop && isTerminal(next.state)) {
            void invalidateRef.current(next.kind);
          }
        }
      })
      .catch((e: unknown) => {
        if (current.current === id && e instanceof FetchError && e.status === 404) {
          setLost(true);
        }
      });
  }, []);

  useEffect(() => {
    setJob(null);
    setLost(false);
    if (jobId) {
      read(jobId, false);
    }
  }, [jobId, read]);

  const listeners = useMemo(() => {
    const on = (dto: BackupJobDto) => {
      const next = normalizeJob(dto);
      setJob(next);
      if (isTerminal(next.state)) {
        void invalidateAfter(next.kind);
      }
    };
    return { progress: on, done: on, error: on, cancelled: on };
  }, [invalidateAfter]);

  const follow = !!jobId && !lost && !(job && job.jobId === jobId && isTerminal(job.state));
  const connection = useEventSource(follow ? `/api/backups/jobs/${jobId}/stream` : null, listeners);

  // A drop (connecting/open → closed) while still following: is the job still there, and still running?
  const previous = useRef(connection);
  useEffect(() => {
    const dropped = previous.current !== 'closed' && connection === 'closed';
    previous.current = connection;
    if (dropped && follow && jobId) {
      read(jobId, true);
    }
  }, [connection, follow, jobId, read]);

  return { job: job && job.jobId === jobId ? job : null, lost };
}
