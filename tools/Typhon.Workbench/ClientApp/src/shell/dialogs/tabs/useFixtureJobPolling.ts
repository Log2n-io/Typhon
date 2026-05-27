import { useEffect, useState } from 'react';
import { customFetch } from '@/api/client';

/**
 * Mirror of the C# `FixtureJobStateDto`. The server's `GET /api/fixtures/jobs/{id}` returns this exact shape.
 * `state` walks `queued → running → done | error | cancelled`; the latter three are terminal.
 */
export interface FixtureJobState {
  jobId: string;
  state: 'queued' | 'running' | 'done' | 'error' | 'cancelled';
  phase: string;
  completed: number;
  total: number;
  /** Populated on `state === 'done'`. */
  result: {
    typhonFilePath: string;
    schemaDllPath: string;
    totalEntities: number;
    wasCreated: boolean;
  } | null;
  /** Populated on `state === 'error'`. */
  error: string | null;
}

const TERMINAL_STATES: ReadonlySet<FixtureJobState['state']> = new Set(['done', 'error', 'cancelled']);

const POLL_INTERVAL_MS = 300;

/**
 * Poll `GET /api/fixtures/jobs/{jobId}` every ~300 ms until the job reaches a terminal state, then stop.
 * Returns the last-known state (null until the first poll lands). Polling is keyed on `jobId` — re-binding
 * the hook with a different id starts a fresh polling cycle and discards the previous one.
 *
 * Caller is responsible for sending the initial `POST /api/fixtures/create` and surfacing the returned
 * `jobId` to this hook. Cancellation lives on the same job-id contract — see {@link cancelFixtureJob}.
 */
export function useFixtureJobPolling(jobId: string | null): FixtureJobState | null {
  const [state, setState] = useState<FixtureJobState | null>(null);

  useEffect(() => {
    if (!jobId) {
      setState(null);
      return;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const poll = async (): Promise<void> => {
      try {
        const { data } = await customFetch<{ data: FixtureJobState }>(`/api/fixtures/jobs/${jobId}`, { method: 'GET' });
        if (cancelled) return;
        setState(data);
        if (TERMINAL_STATES.has(data.state)) return; // stop polling — caller observes via the returned state
        timer = setTimeout(() => { void poll(); }, POLL_INTERVAL_MS);
      } catch {
        // 404 → the job is gone (server restart, prune); surface a synthetic error state so the UI can recover.
        if (cancelled) return;
        setState((prev) => prev ?? { jobId, state: 'error', phase: '', completed: 0, total: 0, result: null, error: 'Job not found' });
      }
    };
    void poll();

    return () => {
      cancelled = true;
      if (timer !== null) clearTimeout(timer);
    };
  }, [jobId]);

  return state;
}

/** Fire-and-forget DELETE — the server's cancellation token unwinds the background Task at the next sub-batch boundary. */
export async function cancelFixtureJob(jobId: string): Promise<void> {
  try {
    await customFetch(`/api/fixtures/jobs/${jobId}`, { method: 'DELETE' });
  } catch {
    /* 404 OK — job already terminated; cancellation is idempotent */
  }
}
