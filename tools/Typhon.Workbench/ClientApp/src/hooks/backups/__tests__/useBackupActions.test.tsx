// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FetchError } from '@/api/client';
import type { BackupRestorePlanDto, BackupRestoreRequestDto } from '@/api/generated/model';
import { useBackupsStore } from '@/stores/useBackupsStore';
import { restoreRequestOf } from '@/panels/Backups/restoreWizardModel';

/**
 * #1417 §4.2: the operator's acts. A plan answer belongs to the inputs it was asked for — one that comes back after the operator changed them is dropped,
 * not shown against the new fields — and a start the server refuses as busy is a refusal, never a start: the wizard stays open, and the card attaches only
 * to a job of this store.
 */

const api = vi.hoisted(() => ({ plan: vi.fn(), create: vi.fn(), restore: vi.fn() }));

vi.mock('@/api/generated/backups/backups', () => ({
  postApiBackupsRestorePlan: api.plan,
  postApiBackupsJobsCreate: api.create,
  postApiBackupsJobsRestore: api.restore,
  postApiBackupsJobsVerify: vi.fn(),
  postApiBackupsJobsDrill: vi.fn(),
  postApiBackupsJobsCompact: vi.fn(),
  postApiBackupsJobsRetention: vi.fn(),
  postApiBackupsRetentionPlan: vi.fn(),
  postApiBackupsArchiveResolveTime: vi.fn(),
  postApiBackupsArchiveLog: vi.fn(),
  deleteApiBackupsJobsJobId: vi.fn(),
}));

import { useBackupActions } from '../useBackupActions';

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const plan = (fingerprint: string): { data: BackupRestorePlanDto } => ({
  data: { canRestore: true, fingerprint, chain: [1], nearestRestorable: [] } as unknown as BackupRestorePlanDto,
});

beforeEach(() => {
  useBackupsStore.setState({
    bundlePath: 'C:/db/world.typhon',
    storePath: 'D:/store',
    activeJobId: null,
    restorePlan: null,
  });
  useBackupsStore.getState().openWizard(2);
  useBackupsStore.getState().updateRestore({ target: 'C:/restored' });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('useBackupActions', () => {
  it('a preview answered after its inputs changed is dropped; one of the current inputs is kept', async () => {
    let answer: (v: { data: BackupRestorePlanDto }) => void = () => {};
    api.plan.mockReturnValueOnce(new Promise((r) => (answer = r)));
    const { result } = renderHook(() => useBackupActions(), { wrapper });
    const s = useBackupsStore.getState();
    const asked = restoreRequestOf(s.storePath, s.bundlePath, s.restore, null) as BackupRestoreRequestDto;

    act(() => result.current.planRestore.mutate(asked));
    act(() => useBackupsStore.getState().updateRestore({ target: 'C:/elsewhere' }));
    await act(async () => answer(plan('old')));
    await waitFor(() => expect(result.current.planRestore.isSuccess).toBe(true));
    expect(useBackupsStore.getState().restorePlan).toBeNull();

    api.plan.mockResolvedValueOnce(plan('new'));
    const t = useBackupsStore.getState();
    act(() =>
      result.current.planRestore.mutate(
        restoreRequestOf(t.storePath, t.bundlePath, t.restore, null) as BackupRestoreRequestDto,
      ),
    );
    await waitFor(() => expect(useBackupsStore.getState().restorePlan?.fingerprint).toBe('new'));
  });

  it('a busy refusal is an error, attached to only when the job in the way is this store’s', async () => {
    const { result } = renderHook(() => useBackupActions(), { wrapper });

    api.create.mockRejectedValueOnce(
      new FetchError(409, {
        title: 'busy',
        detail: 'A drill is already running in this Workbench',
        runningJobId: 'other',
        sameStore: false,
      }),
    );
    act(() => result.current.create.mutate({ store: 'D:/store', bundle: '', full: false }));
    await waitFor(() => expect(result.current.create.isError).toBe(true));
    expect(useBackupsStore.getState().activeJobId).toBeNull();

    api.create.mockRejectedValueOnce(
      new FetchError(409, {
        title: 'busy',
        detail: 'A verify job is already running on this store',
        runningJobId: 'mine',
        sameStore: true,
      }),
    );
    act(() => result.current.create.mutate({ store: 'D:/store', bundle: '', full: false }));
    await waitFor(() => expect(useBackupsStore.getState().activeJobId).toBe('mine'));
    expect(result.current.create.isError).toBe(true);
    expect(result.current.create.isSuccess).toBe(false);
  });
});
