// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetchError } from '@/api/client';
import type { BackupJobDto } from '@/api/generated/model';

/**
 * #1417 §3.5: following one job. The stream's connection is staged by hand (jsdom has no EventSource): what matters is what the hook does when it drops —
 * a Workbench restarted mid-job no longer knows the job, and the card must say "lost" instead of reconnecting every 3 s forever.
 */

const api = vi.hoisted(() => ({ job: vi.fn() }));
const stream = vi.hoisted(() => ({ state: 'closed' as 'connecting' | 'open' | 'closed', url: null as string | null }));

vi.mock('@/api/generated/backups/backups', () => ({ getApiBackupsJobsJobId: api.job }));
vi.mock('@/hooks/streams/useEventSource', () => ({
  useEventSource: (url: string | null) => {
    stream.url = url;
    return url ? stream.state : 'closed';
  },
}));

import { useBackupJob } from '../useBackupJob';

const running: { data: BackupJobDto } = {
  data: {
    jobId: 'j1',
    kind: 'restore',
    store: 'D:/store',
    state: 'running',
    phase: 'restore',
    completed: 3,
    total: 10,
    startedUtc: '2026-10-21T10:00:00Z',
  } as unknown as BackupJobDto,
};

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
  stream.state = 'closed';
  stream.url = null;
});

describe('useBackupJob', () => {
  it('a stream that drops re-reads the job; a 404 then means the job is lost, and the stream is let go', async () => {
    api.job.mockResolvedValueOnce(running);
    stream.state = 'connecting';
    const hook = renderHook(() => useBackupJob('j1'), { wrapper });
    await waitFor(() => expect(hook.result.current.job?.state).toBe('running'));
    expect(stream.url).toBe('/api/backups/jobs/j1/stream');

    stream.state = 'open';
    hook.rerender();
    api.job.mockRejectedValueOnce(new FetchError(404, { title: 'Not Found' }));
    act(() => {
      stream.state = 'closed';
    });
    hook.rerender();

    await waitFor(() => expect(hook.result.current.lost).toBe(true));
    expect(api.job).toHaveBeenCalledTimes(2);
    expect(stream.url).toBeNull();
  });

  it('a stream that drops while the job still runs keeps following it', async () => {
    api.job.mockResolvedValue(running);
    stream.state = 'open';
    const hook = renderHook(() => useBackupJob('j1'), { wrapper });
    await waitFor(() => expect(hook.result.current.job?.state).toBe('running'));

    stream.state = 'closed';
    hook.rerender();

    await waitFor(() => expect(api.job).toHaveBeenCalledTimes(2));
    expect(hook.result.current.lost).toBe(false);
    expect(stream.url).toBe('/api/backups/jobs/j1/stream');
  });
});
